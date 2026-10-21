using System.Text.Json;
using AiReap.Application.Artifacts;

namespace AiReap.Application.Ai.Pipeline;

// P0-7 — how complete an Analyze or Generate result is, returned with it. Response-only: nothing here
// is stored (that is P3-1), and nothing is recomputed - it reports what the AI call and the P0-2 evidence
// check of this run already produced. Source-ID coverage is not repeated here; it is the result's
// SourceIdCoverage (P0-4).
//
// ContextReduction: "none" - the source was sent whole in one request; "split" - it was sent in
// PartCount parts at section and paragraph boundaries, every passage exactly once, so nothing was left
// out but the AI could not reason across parts. These two operations never condense: a source that
// can be neither sent whole nor split is refused (GenerationSupport.CallAiAndParseChunkedAsync).
//
// Clarifications (the answered clarification Q&A Generate sends with the source): "whole" - every
// request carried all of them; "distributed" - the source was split and each part carried the ones
// most relevant to it, every one going with at least one part; "none" - there were none to send
// (always for Analyze).
//
// Models: the distinct models that served the requests, as the provider client reported them; empty
// when unreported. No provider is inferred.
//
// Evidence counts (Generate only; null for Analyze), over the requirements this run CREATED - not the
// duplicates it skipped (SkippedDuplicates) and not requirements saved earlier:
//   UnverifiedEvidenceCount - evidenceStatus "unverified": at least one quote was not found as written;
//   NoEvidenceCount         - evidenceStatus "none" (or missing): no quote was given;
//   UnsupportedNumbersCount - requirements with at least one number or quantity that none of its
//                             verified quotes contains (unsupportedNumbers non-empty).
public sealed record RequirementCompleteness(
    string ContextReduction,
    int PartCount,
    string Clarifications,
    IReadOnlyList<string> Models,
    int? UnverifiedEvidenceCount = null,
    int? NoEvidenceCount = null,
    int? UnsupportedNumbersCount = null)
{
    public const string NoReduction = "none", Split = "split";
    public const string ClarificationsWhole = "whole", ClarificationsDistributed = "distributed", ClarificationsNone = "none";

    // From the parts CallAiAndParseChunkedAsync returned; clarificationCount is the number of shared
    // context items the call was given.
    public static RequirementCompleteness ForParts<T>(IReadOnlyList<GenerationSupport.AiPart<T>> parts, int clarificationCount) => new(
        parts.Count > 1 ? Split : NoReduction,
        parts.Count,
        clarificationCount == 0 ? ClarificationsNone
            : parts.All(p => p.SharedContextWhole) ? ClarificationsWhole : ClarificationsDistributed,
        parts.Select(p => p.Model).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.Ordinal).ToList());

    // The evidence counts over the requirements this run created, read from their saved P0-2 fields.
    public RequirementCompleteness WithEvidence(IEnumerable<ArtifactResponse> created)
    {
        int unverified = 0, none = 0, numbers = 0;
        foreach (var artifact in created)
        {
            var status = artifact.Data.ValueKind == JsonValueKind.Object
                && artifact.Data.TryGetProperty("evidenceStatus", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString()
                    : null;
            if (status == RequirementEvidence.Unverified) unverified++;
            else if (status != RequirementEvidence.Verified) none++;

            if (artifact.Data.ValueKind == JsonValueKind.Object
                && artifact.Data.TryGetProperty("unsupportedNumbers", out var n) && n.ValueKind == JsonValueKind.Array && n.GetArrayLength() > 0)
            {
                numbers++;
            }
        }

        return this with { UnverifiedEvidenceCount = unverified, NoEvidenceCount = none, UnsupportedNumbersCount = numbers };
    }
}
