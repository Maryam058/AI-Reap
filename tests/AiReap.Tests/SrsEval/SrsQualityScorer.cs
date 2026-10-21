using System.Text.Json;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;

namespace AiReap.Tests.SrsEval;

// Phase 0 — deterministic quality scoring of requirement-analysis output against a golden fixture.
// No AI call and no I/O. Every quote is re-checked against the source with the product's own checker
// (RequirementEvidence), so a candidate cannot claim its own evidence results.
//
// Matching a generated requirement to an expected one is one-to-one and tried in three passes, most
// reliable first; the method is reported per match:
//   SourceId       the item cites a source ID the expected requirement is written under, and that ID
//                  is defined in the source (a valid citation);
//   VerifiedQuote  a quote of the item that the checker verified in the SRS overlaps an anchor quote;
//   KeywordOnly    every expected keyword appears in the item's normalized title and claim.
// A KeywordOnly match counts toward recall only: never toward grounded recall, never as verified
// evidence and never as a valid citation. A second item matched to an already matched requirement is
// an unmerged duplicate, not a new requirement.
//
// Contradiction and gap detection read clarification text only (the analysis schema has no quotes),
// so they are keyword heuristics: detected when one clarification contains a keyword of every group.
public static class SrsQualityScorer
{
    public const string SourceIdMatch = "SourceId", VerifiedQuoteMatch = "VerifiedQuote", KeywordOnlyMatch = "KeywordOnly";
    public const string Duplicate = "Duplicate", BackgroundPromoted = "BackgroundPromoted", AllowedDerived = "AllowedDerived", Unmatched = "Unmatched";

    // Two clarifications are redundant when their merge keys are equal or their word sets overlap this much.
    public const double RedundantCqJaccard = 0.8;

    public static SrsQualityReport Score(string fixture, string sourceText, FixtureExpectation expected, CandidateOutput candidate)
    {
        var defined = SourceIdCoverage.Classify(sourceText).Defined.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evidence = RequirementEvidence.Create(sourceText);
        var items = candidate.Requirements.Select((c, i) => Assess(i + 1, c, evidence, sourceText)).ToList();

        // ---- matching -------------------------------------------------------------------------
        var primary = new Dictionary<string, AssessedItem>(StringComparer.Ordinal);
        var assignment = new Dictionary<int, (string Outcome, string? Key, string? Method)>();

        void Pass(string method, Func<AssessedItem, ExpectedRequirement, bool> matches)
        {
            foreach (var item in items.Where(i => !assignment.ContainsKey(i.Index)))
            {
                var hit = expected.Requirements.FirstOrDefault(r => matches(item, r));
                if (hit is null) continue;
                if (primary.TryAdd(hit.Key, item)) assignment[item.Index] = (method, hit.Key, method);
                else assignment[item.Index] = (Duplicate, hit.Key, method);
            }
        }

        Pass(SourceIdMatch, (i, r) => i.Candidate.SourceIds.Any(id => defined.Contains(id.Trim()) && r.SourceIds.Contains(id.Trim(), StringComparer.OrdinalIgnoreCase)));
        Pass(VerifiedQuoteMatch, (i, r) => i.VerifiedSrsQuotes.Any(q => r.AnchorQuotes.Any(a => Overlaps(q, a))));
        Pass(KeywordOnlyMatch, (i, r) => r.Keywords.Count > 0 && r.Keywords.All(k => ContainsPhrase(i.ClaimText, k)));

        foreach (var item in items.Where(i => !assignment.ContainsKey(i.Index)))
        {
            var background = expected.NonRequirements.FirstOrDefault(n =>
                item.VerifiedSrsQuotes.Any(q => Overlaps(q, n.AnchorQuote))
                || (n.Keywords.Count > 0 && n.Keywords.All(k => ContainsPhrase(item.ClaimText, k))));
            if (background is not null)
            {
                assignment[item.Index] = (BackgroundPromoted, background.Key, null);
                continue;
            }

            var derived = expected.AllowedDerived.FirstOrDefault(d => d.Keywords.Count > 0 && d.Keywords.All(k => ContainsPhrase(item.ClaimText, k)));
            assignment[item.Index] = derived is null ? (Unmatched, null, null) : (AllowedDerived, derived.Key, null);
        }

        int CountOutcome(string outcome) => assignment.Values.Count(a => a.Outcome == outcome);
        var matched = expected.Requirements.Where(r => primary.ContainsKey(r.Key)).ToList();
        var grounded = matched.Where(r => assignment[primary[r.Key].Index].Method is SourceIdMatch or VerifiedQuoteMatch).ToList();

        Ratio RecallOf(string kind)
        {
            var all = expected.Requirements.Where(r => r.Kind == kind).ToList();
            return new Ratio(all.Count(r => primary.ContainsKey(r.Key)), all.Count);
        }

        // ---- duplicates -----------------------------------------------------------------------
        var duplicates = expected.Duplicates.Select(d =>
        {
            var extra = assignment.Values.Count(a => a.Outcome == Duplicate && a.Key == d.Requirement);
            var first = primary.GetValueOrDefault(d.Requirement);
            var citesAll = first is not null && d.SourceIds.Count > 0
                && d.SourceIds.All(id => first.Candidate.SourceIds.Contains(id, StringComparer.OrdinalIgnoreCase));
            return new DuplicateResult(d.Key, d.Requirement, Handled: first is not null && extra == 0, ExtraItems: extra, CitesAllSourceIds: citesAll);
        }).ToList();

        // ---- stated targets -------------------------------------------------------------------
        var withTarget = expected.Requirements.Where(r => r.StatedTarget is not null).ToList();
        var targetsCorrect = withTarget.Count(r =>
            primary.TryGetValue(r.Key, out var item)
            && string.Equals(item.Candidate.TargetOrigin?.Trim(), RequirementEvidence.Stated, StringComparison.OrdinalIgnoreCase)
            && item.TargetSupport == RequirementEvidence.QuoteFound
            && SameNumbers(r.StatedTarget, item.Candidate.TargetValue));

        // ---- analysis -------------------------------------------------------------------------
        var cqTexts = candidate.Clarifications.Select(c => $"{c.Topic} {c.Question} {c.Reason}").ToList();
        List<string> Detected(IEnumerable<PlantedExpectation> planted) =>
            planted.Where(p => cqTexts.Any(t => p.KeywordGroups.All(g => g.Any(k => ContainsPhrase(t, k))))).Select(p => p.Key).ToList();
        var gapsDetected = Detected(expected.Gaps);
        var contradictionsDetected = Detected(expected.Contradictions);

        var redundant = new List<string>();
        for (var a = 0; a < candidate.Clarifications.Count; a++)
        {
            for (var b = a + 1; b < candidate.Clarifications.Count; b++)
            {
                if (Redundant(candidate.Clarifications[a].Question, candidate.Clarifications[b].Question))
                {
                    redundant.Add($"{a + 1}~{b + 1}");
                }
            }
        }

        var coverage = SourceIdCoverage.Compute(sourceText, candidate.Requirements.Select(c => ((IEnumerable<string>)c.SourceIds, false)));
        var citedIds = items.SelectMany(i => i.Candidate.SourceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())).ToList();

        return new SrsQualityReport
        {
            Fixture = fixture,
            Requirements = new RequirementScores
            {
                Expected = expected.Requirements.Count,
                Generated = items.Count,
                Recall = new Ratio(matched.Count, expected.Requirements.Count),
                GroundedRecall = new Ratio(grounded.Count, expected.Requirements.Count),
                KeywordOnlyMatches = matched.Count - grounded.Count,
                FrRecall = RecallOf("FR"),
                NfrRecall = RecallOf("NFR"),
                Precision = new Ratio(matched.Count + CountOutcome(AllowedDerived), items.Count),
                UnmergedDuplicates = CountOutcome(Duplicate),
                BackgroundPromoted = CountOutcome(BackgroundPromoted),
                Unmatched = CountOutcome(Unmatched),
                AllowedDerived = CountOutcome(AllowedDerived),
                Missing = expected.Requirements.Where(r => !primary.ContainsKey(r.Key)).Select(r => r.Key).ToList()
            },
            Citations = new CitationScores
            {
                QuotesVerified = new Ratio(items.Sum(i => i.VerifiedQuoteCount), items.Sum(i => i.QuoteCount)),
                ItemsWithVerifiedSrsQuote = new Ratio(items.Count(i => i.VerifiedSrsQuotes.Count > 0), items.Count),
                CitedSourceIdsDefined = new Ratio(citedIds.Count(defined.Contains), citedIds.Count),
                ExplicitWithoutVerifiedSrsQuote = items.Count(i =>
                    string.Equals(i.Candidate.Origin?.Trim(), "explicit", StringComparison.OrdinalIgnoreCase) && i.VerifiedSrsQuotes.Count == 0),
                CoverageStatus = coverage.Status,
                UncoveredSourceIds = coverage.Uncovered.ToList()
            },
            Numbers = new NumberScores
            {
                ItemsWithUnsupportedNumbers = items.Count(i => i.UnsupportedNumbers.Count > 0),
                ItemsWithNumbersNotInSource = items.Count(i => i.NumbersNotInSource.Count > 0),
                StatedTargetsNotSupported = items.Count(i => i.TargetSupport == RequirementEvidence.NotSupported),
                StatedTargetAccuracy = new Ratio(targetsCorrect, withTarget.Count)
            },
            Duplicates = duplicates,
            Analysis = new AnalysisScores
            {
                Clarifications = candidate.Clarifications.Count,
                GapRecall = new Ratio(gapsDetected.Count, expected.Gaps.Count),
                GapsDetected = gapsDetected,
                GapsMissed = expected.Gaps.Select(g => g.Key).Except(gapsDetected).ToList(),
                ContradictionRecallHeuristic = new Ratio(contradictionsDetected.Count, expected.Contradictions.Count),
                ContradictionsDetected = contradictionsDetected,
                ContradictionsMissed = expected.Contradictions.Select(c => c.Key).Except(contradictionsDetected).ToList(),
                RedundantClarificationPairs = redundant
            },
            Items = items.Select(i =>
            {
                var (outcome, key, _) = assignment[i.Index];
                return new ItemResult(i.Index, i.Candidate.Kind, i.Candidate.Title, outcome, key, i.EvidenceStatus,
                    i.UnsupportedNumbers, i.NumbersNotInSource, i.TargetSupport);
            }).ToList()
        };
    }

    // ---- adapters ---------------------------------------------------------------------------

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    // The FRs, NFRs and clarification questions a service persisted, as a candidate (rejected ones left
    // out). The FR claim is the same text the product checks (RequirementEvidence.FunctionalClaim).
    public static CandidateOutput FromArtifacts(IEnumerable<Artifact> artifacts)
    {
        var list = artifacts.Where(a => a.Status != ArtifactStatus.Rejected).OrderBy(a => a.ArtifactType).ThenBy(a => a.Code, StringComparer.Ordinal).ToList();
        var requirements = new List<CandidateRequirement>();
        foreach (var a in list)
        {
            if (a.ArtifactType == ArtifactType.FunctionalRequirement)
            {
                var p = JsonSerializer.Deserialize<FunctionalRequirementPayload>(a.DataJson, ReadOptions)!;
                requirements.Add(new CandidateRequirement
                {
                    Kind = "FR", Title = a.Title, Actor = p.Actor, SourceIds = p.SourceIds, Origin = p.RequirementOrigin,
                    Text = RequirementEvidence.FunctionalClaim(p.Preconditions, p.Inputs, p.Processing, p.ExpectedResult),
                    Evidence = p.Evidence.Select(ToQuote).ToList()
                });
            }
            else if (a.ArtifactType == ArtifactType.NonFunctionalRequirement)
            {
                var p = JsonSerializer.Deserialize<NonFunctionalRequirementPayload>(a.DataJson, ReadOptions)!;
                requirements.Add(new CandidateRequirement
                {
                    Kind = "NFR", Title = a.Title, Category = p.Category, Text = p.Description ?? string.Empty, SourceIds = p.SourceIds,
                    Origin = p.RequirementOrigin, Evidence = p.Evidence.Select(ToQuote).ToList(),
                    TargetValue = p.TargetValue, TargetOrigin = p.TargetOrigin
                });
            }
        }

        var clarifications = list.Where(a => a.ArtifactType == ArtifactType.ClarificationQuestion).Select(a =>
        {
            var p = JsonSerializer.Deserialize<ClarificationQuestionPayload>(a.DataJson, ReadOptions)!;
            return new CandidateClarification { Topic = a.Title, Question = p.Question, Reason = p.Reason ?? string.Empty };
        }).ToList();

        return new CandidateOutput { Requirements = requirements, Clarifications = clarifications };
    }

    private static CandidateQuote ToQuote(EvidenceQuotePayload q) => new() { From = q.From, Quote = q.Quote, SourceId = q.SourceId, Ref = q.Ref };

    // ---- per-item assessment ----------------------------------------------------------------

    private sealed record AssessedItem(
        int Index, CandidateRequirement Candidate, string ClaimText, string EvidenceStatus, int QuoteCount, int VerifiedQuoteCount,
        List<string> VerifiedSrsQuotes, List<string> UnsupportedNumbers, List<string> NumbersNotInSource, string? TargetSupport);

    private static AssessedItem Assess(int index, CandidateRequirement c, RequirementEvidence evidence, string sourceText)
    {
        var (status, quotes) = evidence.Assess(c.Evidence.Select(q => new EvidenceQuotePayload { From = q.From, Quote = q.Quote, SourceId = q.SourceId, Ref = q.Ref }));
        var claim = c.Kind == "NFR" ? RequirementEvidence.NonFunctionalClaim(c.Text, c.TargetValue) : c.Text;
        var verifiedSrs = quotes
            .Where(q => RequirementEvidence.IsVerified(q) && string.Equals(q.From?.Trim(), RequirementEvidence.FromSrs, StringComparison.OrdinalIgnoreCase))
            .Select(q => q.Quote).ToList();
        return new AssessedItem(
            index, c, $"{c.Title}\n{claim}", status, quotes.Count, quotes.Count(RequirementEvidence.IsVerified), verifiedSrs,
            RequirementEvidence.UnsupportedNumbers(claim, quotes),
            SourceEvidence.NumbersMissingFromEvidence(claim, [sourceText]).ToList(),
            c.Kind == "NFR" ? RequirementEvidence.TargetSupportFor(c.TargetOrigin, c.TargetValue, quotes) : null);
    }

    // ---- text helpers -----------------------------------------------------------------------

    // Whole-token phrase containment in the checker's normalized form (case, punctuation, hyphens).
    public static bool ContainsPhrase(string text, string phrase)
    {
        var needle = SourceEvidence.Normalize(phrase);
        return needle.Length > 0 && $" {SourceEvidence.Normalize(text)} ".Contains($" {needle} ", StringComparison.Ordinal);
    }

    private static bool Overlaps(string quote, string anchor) => ContainsPhrase(quote, anchor) || ContainsPhrase(anchor, quote);

    private static bool SameNumbers(string? expected, string? actual) =>
        !string.IsNullOrWhiteSpace(actual)
        && SourceEvidence.NumbersMissingFromEvidence(expected, [actual]).Count == 0
        && SourceEvidence.NumbersMissingFromEvidence(actual, [expected]).Count == 0;

    private static bool Redundant(string a, string b)
    {
        if (AiResultMerge.Key(a) == AiResultMerge.Key(b)) return true;
        var wa = SourceEvidence.Normalize(a).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wb = SourceEvidence.Normalize(b).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (wa.Count == 0 || wb.Count == 0) return false;
        var shared = wa.Count(wb.Contains);
        return shared / (double)(wa.Count + wb.Count - shared) >= RedundantCqJaccard;
    }
}
