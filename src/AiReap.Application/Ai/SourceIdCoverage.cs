using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Ai;

// P0-4 — which requirement IDs written in a requirement source no saved requirement cites, so a
// requirement the model skipped is visible instead of silently missing. Deterministic; no AI call.
//
// IDs are those SrsIdExtractor finds (its pattern is unchanged), classified by POSITION, never by
// prefix - there is no list of excluded names:
//   defined         the ID starts a line, after an optional bullet, list number, "|" (a table's first
//                   cell) or bracket - the rule SourceEvidenceIndex uses for an ID's passage. Only
//                   defined IDs are expected to be covered.
//   mentioned only  the ID is never defined, only written inside text ("hashed with SHA-256",
//                   "see FR-003"). Listed separately and never counted as missing or covered.
// An ID is covered when a saved FR or NFR of the source that is not Rejected cites it in sourceIds
// (dependsOnSourceIds does not count). IDs cited only by rejected requirements are uncovered, and
// listed again in CoveredOnlyByRejected. A source with no defined ID is NotApplicable: coverage can
// not be checked, which is never reported as complete. IDs compare case-insensitively and are
// listed in source order, as written in the source.
//
// Limits: an ID defined in another layout (a "## FR-001" heading, "Requirement FR-001: ...") is
// reported mentioned-only; a standards table whose first column is "ISO-27001" reports it defined.
public sealed partial record SourceIdCoverage(
    string Status,
    int DefinedCount,
    IReadOnlyList<string> Covered,
    IReadOnlyList<string> Uncovered,
    IReadOnlyList<string> CoveredOnlyByRejected,
    IReadOnlyList<string> MentionedOnly)
{
    public const string NotApplicable = "not_applicable";
    public const string Complete = "complete";
    public const string Incomplete = "incomplete";

    // The source's IDs, in order of first appearance: those defined somewhere, and those only mentioned.
    public static (IReadOnlyList<string> Defined, IReadOnlyList<string> MentionedOnly) Classify(string? sourceText)
    {
        var text = sourceText ?? string.Empty;
        var all = SrsIdExtractor.Extract(text);
        if (all.Count == 0)
        {
            return ([], []);
        }

        var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var rest = line[LinePrefix().Match(line).Length..];
            var id = SrsIdExtractor.Extract(line).Where(i => StartsWithId(rest, i)).MaxBy(i => i.Length);
            if (id is not null)
            {
                defined.Add(id);
            }
        }

        return (all.Where(defined.Contains).ToList(), all.Where(i => !defined.Contains(i)).ToList());
    }

    // requirements: each saved FR/NFR's sourceIds and whether it is Rejected.
    public static SourceIdCoverage Compute(string? sourceText, IEnumerable<(IEnumerable<string> SourceIds, bool Rejected)> requirements)
    {
        var (defined, mentionedOnly) = Classify(sourceText);
        var cited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var citedByRejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sourceIds, rejected) in requirements)
        {
            foreach (var id in sourceIds.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                (rejected ? citedByRejected : cited).Add(id.Trim());
            }
        }

        var covered = defined.Where(cited.Contains).ToList();
        var uncovered = defined.Where(id => !cited.Contains(id)).ToList();
        var status = defined.Count == 0 ? NotApplicable : uncovered.Count == 0 ? Complete : Incomplete;
        return new SourceIdCoverage(status, defined.Count, covered, uncovered, uncovered.Where(citedByRejected.Contains).ToList(), mentionedOnly);
    }

    // The coverage of a source by the FRs and NFRs saved for it now, as stored (generated or edited).
    public static async Task<SourceIdCoverage> ForSourceAsync(IAiReapDbContext db, RequirementSource source, CancellationToken cancellationToken)
    {
        var requirements = await db.Artifacts.AsNoTracking()
            .Where(a => a.RequirementSourceId == source.Id
                        && (a.ArtifactType == ArtifactType.FunctionalRequirement || a.ArtifactType == ArtifactType.NonFunctionalRequirement))
            .Select(a => new { a.Status, a.DataJson })
            .ToListAsync(cancellationToken);
        return Compute(source.RawText, requirements.Select(r => (SourceIdsOf(r.DataJson), r.Status == ArtifactStatus.Rejected)));
    }

    // A payload's sourceIds, whatever the key's case (generation stores PascalCase, edits camelCase);
    // anything that is not an array of strings cites nothing.
    private static IEnumerable<string> SourceIdsOf(string dataJson)
    {
        JsonNode? payload;
        try
        {
            payload = JsonNode.Parse(dataJson);
        }
        catch (JsonException)
        {
            return [];
        }

        var ids = (payload as JsonObject)?.FirstOrDefault(kv => kv.Key.Equals("SourceIds", StringComparison.OrdinalIgnoreCase)).Value;
        return ids is JsonArray array
            ? array.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList()
            : [];
    }

    // The ID is the whole first token of the line, not the start of a longer one ("FR-1" in "FR-12").
    private static bool StartsWithId(string rest, string id) =>
        rest.StartsWith(id, StringComparison.OrdinalIgnoreCase)
        && (rest.Length == id.Length || !(char.IsLetterOrDigit(rest[id.Length]) || rest[id.Length] == '-'));

    // Optional indentation, bullets, "|" table separators or an opening bracket, then an optional list
    // number ("1.", "2)", "3.2.1") - the same prefix SourceEvidenceIndex recognises.
    [GeneratedRegex(@"^[\s\-*•·▪◦|\[(]*(?:\d+(?:\.\d+)*[.)]?\s+)?")]
    private static partial Regex LinePrefix();
}
