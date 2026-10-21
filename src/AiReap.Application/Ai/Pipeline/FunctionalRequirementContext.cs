using System.Text;
using System.Text.Json;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Domain.Entities;

namespace AiReap.Application.Ai.Pipeline;

// A4b Phase 3 — how the acceptance-criteria and test-case prompts describe a functional requirement
// beyond its own text: its origin and the server's evidence results, labelled from the persisted
// fields only (RequirementEvidence). Quote text is never included: a restricted caller may not read
// it (RequirementEvidenceFields.RedactForRestrictedCaller), and anything in a prompt can be copied into
// generated criteria and tests that caller does read. A record made before evidence checks (no
// EvidenceCheck) is labelled not checked - its empty fields are defaults, not a passed check.
public static class FunctionalRequirementContext
{
    public const string NotChecked = "not checked (created before evidence checks)";
    public const string NotSpecified = "not specified";
    public const string NotRecorded = "not recorded";
    public const string NoQuotes = "no quotes given";

    // The heading of the linked-requirement block; the links are traceability, not source evidence.
    public const string LinkedHeading =
        "Linked functional requirements (traceability links from this story; a link is not source evidence):";

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static FunctionalRequirementPayload Read(Artifact functionalRequirement) =>
        JsonSerializer.Deserialize<FunctionalRequirementPayload>(functionalRequirement.DataJson, ReadOptions) ?? new FunctionalRequirementPayload();

    public static string Inputs(FunctionalRequirementPayload payload) => OrLabel(payload.Inputs, NotSpecified);

    public static string Origin(FunctionalRequirementPayload payload) => OrLabel(payload.RequirementOrigin, NotRecorded);

    public static string EvidenceStatus(FunctionalRequirementPayload payload)
    {
        if (payload.EvidenceCheck is null) return NotChecked;
        return payload.EvidenceStatus?.Trim().ToLowerInvariant() switch
        {
            RequirementEvidence.Verified => "verified (its quotes were found in the cited text; this does not confirm the requirement itself)",
            RequirementEvidence.Unverified => "unverified (at least one of its quotes was not found in the cited text)",
            RequirementEvidence.None => NoQuotes,
            null or "" => NotRecorded,
            var other => other
        };
    }

    public static string UnsupportedNumbers(FunctionalRequirementPayload payload)
    {
        if (payload.EvidenceCheck is null) return NotChecked;
        var numbers = payload.UnsupportedNumbers.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        return numbers.Count == 0 ? "none" : string.Join(", ", numbers);
    }

    // The lines a test-case prompt appends after its existing fields.
    public static string EvidenceLines(FunctionalRequirementPayload payload) =>
        $"\nInputs: {Inputs(payload)}\nRequirement origin: {Origin(payload)}\nEvidence status: {EvidenceStatus(payload)}\n" +
        $"Numbers not found in verified quotes: {UnsupportedNumbers(payload)}";

    // The block an acceptance-criteria prompt appends for a story's eligible linked requirements, in
    // code order (ordinal) so the prompt is deterministic; empty when there are none.
    public static string LinkedBlock(IEnumerable<Artifact> eligibleFunctionalRequirements)
    {
        var frs = eligibleFunctionalRequirements.DistinctBy(a => a.Id).OrderBy(a => a.Code, StringComparer.Ordinal).ToList();
        if (frs.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder($"\n\n{LinkedHeading}\n");
        foreach (var fr in frs)
        {
            var p = Read(fr);
            sb.Append($"- {fr.Code}: {fr.Title}\n");
            sb.Append($"  Actor: {OrLabel(p.Actor, NotSpecified)}\n");
            sb.Append($"  Preconditions: {OrLabel(p.Preconditions, NotSpecified)}\n");
            sb.Append($"  Inputs: {Inputs(p)}\n");
            sb.Append($"  Processing: {OrLabel(p.Processing, NotSpecified)}\n");
            sb.Append($"  Expected result: {OrLabel(p.ExpectedResult, NotSpecified)}\n");
            sb.Append($"  Requirement origin: {Origin(p)}\n");
            sb.Append($"  Evidence status: {EvidenceStatus(p)}\n");
            sb.Append($"  Numbers not found in verified quotes: {UnsupportedNumbers(p)}\n");
        }

        return sb.ToString();
    }

    private static string OrLabel(string? value, string label) => string.IsNullOrWhiteSpace(value) ? label : value.Trim();
}
