using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai.Pipeline;
using AiReap.Domain.Enums;

namespace AiReap.Application.Artifacts;

// G3 — whether a functional or non-functional requirement's stored evidence and NFR target allow it
// to be approved: the artifact's own approval (ArtifactService.UpdateStatusAsync) and the Design and
// Testing exit checks (LifecycleEvidenceService) both ask this, so the two can never disagree. Read
// from the persisted fields only; nothing is recomputed, inferred or written back.
//
//   EvidenceStatus "unverified"                       blocks: a cited quote was not found in its source
//   EvidenceStatus "none", origin derived/assumed     passes, reported as not source-verified
//   EvidenceStatus "none", origin explicit, missing   blocks: declared (or not declared) as stated in the
//     or unrecognized                                 source, with no quote. An origin is never inferred.
//   no EvidenceCheck / no recognized EvidenceStatus   passes, reported as unchecked (made before checks)
//   NFR TargetSupport "not_supported", unconfirmed    blocks until a human confirms the target
//   NFR target present and not confirmed              blocks until a human confirms the target
//
// "verified" only means the quoted words are in the cited text, not that the requirement is right.
// Blocker codes and Describe texts never carry quote text, so they are safe for every caller.
public static class RequirementApprovalEvidence
{
    public const string EvidenceUnverified = "evidence-unverified";
    public const string EvidenceMissing = "evidence-missing";
    public const string TargetNotSupported = "target-not-supported";
    public const string TargetUnconfirmed = "target-unconfirmed";

    private const string Explicit = "explicit", Derived = "derived", Assumed = "assumed";

    public sealed record Assessment(
        // Blocking codes, most serious first; empty when approval may go ahead.
        IReadOnlyList<string> Blockers,
        // Made before evidence checks (or with no recognized status): not verified, not blocked.
        bool EvidenceUnchecked,
        // "derived" or "assumed" with no quote: allowed, but not source-verified.
        string? OriginWithoutQuote)
    {
        public bool Blocks => Blockers.Count > 0;
    }

    public static bool Applies(ArtifactType type) => RequirementEvidenceFields.Applies(type);

    public static Assessment Assess(ArtifactType type, string dataJson)
    {
        var payload = ParseObject(dataJson);
        var blockers = new List<string>();
        var evidenceUnchecked = false;
        string? originWithoutQuote = null;

        var status = Normalize(Text(payload, "EvidenceStatus"));
        var checkedRecord = !string.IsNullOrWhiteSpace(Text(payload, "EvidenceCheck"));
        if (status == RequirementEvidence.Unverified)
        {
            blockers.Add(EvidenceUnverified);
        }
        else if (!checkedRecord || status is not (RequirementEvidence.Verified or RequirementEvidence.None))
        {
            evidenceUnchecked = true;
        }
        else if (status == RequirementEvidence.None)
        {
            var origin = Normalize(Text(payload, "RequirementOrigin"));
            if (origin is Derived or Assumed) originWithoutQuote = origin;
            else blockers.Add(EvidenceMissing);
        }

        if (type == ArtifactType.NonFunctionalRequirement)
        {
            var confirmed = Text(payload, "AssumptionStatus") == NfrAssumption.Confirmed;
            if (!confirmed && Normalize(Text(payload, "TargetSupport")) == RequirementEvidence.NotSupported)
            {
                blockers.Add(TargetNotSupported);
            }
            else if (!confirmed && !string.IsNullOrWhiteSpace(Text(payload, "TargetValue")))
            {
                blockers.Add(TargetUnconfirmed);
            }
        }

        return new Assessment(blockers, evidenceUnchecked, originWithoutQuote);
    }

    public static string Describe(string code) => code switch
    {
        EvidenceUnverified => "a cited source quote was not found in the text it cites",
        EvidenceMissing => "classified as stated in the source (or origin not recorded) but cites no source quote",
        TargetNotSupported => "the target is claimed as stated but no verified source quote supports it, and it is not confirmed",
        TargetUnconfirmed => "the target value has not been confirmed",
        _ => code
    };

    // Why an artifact approval is refused. Artifact approval has no override; the stage gates do.
    public static string ApprovalRefusal(string code, Assessment assessment) =>
        $"{code} cannot be approved: {string.Join("; ", assessment.Blockers.Select(b => $"{b} ({Describe(b)})"))}. " +
        "Resolve this first (confirm the NFR target, or reclassify the requirement's origin by an edit), or reject it with a reason.";

    private static JsonObject ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static string? Text(JsonObject payload, string key) =>
        payload.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value is JsonValue value
        && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
