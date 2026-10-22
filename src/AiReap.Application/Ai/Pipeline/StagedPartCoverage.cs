using System.Text.RegularExpressions;

namespace AiReap.Application.Ai.Pipeline;

// H1 — staged generation: is a part that produced no requirements actually missing some? Deterministic,
// no AI call. Used by GenerationSupport.CallAiAndParseStagedAsync on parts whose response had no items.
//
// Candidates. The IDs SourceIdCoverage.Classify reports defined in the part (unchanged rule: the ID
// starts a line), except on lines whose layout says they are not definitions - decided by layout only,
// never by wording or by a list of prefixes, so it works the same for every language:
//   table of contents   the line ends in dot leaders, an ellipsis or a tab, then a page number
//                       ("FR-001 Booking ........ 12");
//   cross-reference row a table row with at least two cells that hold nothing but IDs and no cell of
//                       MinProseWords words or more ("| FR-001 | UC-01 | TC-01 |"). A row whose prose
//                       mentions another ID ("| FR-010 | Exports must ... see FR-003 |") still defines.
//
// Coverage. A candidate is covered only when some generated requirement cites it in sourceIds AND has
// an "srs" quote that SourceEvidenceIndex verifies inside that ID's own passage of the full source. A
// citation alone ("FR-120 ... see FR-250" citing both, quoting only FR-120's line) does not cover it.
//
// Verdict for an uncovered candidate, by ID family (the ID without its trailing number: FR, NFR, SEC,
// TA-FR, ISO, REV), compared case-insensitively:
//   missing       another ID of the family is covered in this run: the document's own requirements use
//                 the family, so this one was left out. The run fails.
//   unclassified  no ID of the family is covered anywhere: a glossary, a standards or revision table,
//                 a test-case appendix - or a family that exists only in this part (known gap). The run
//                 is not failed; the ID stays visible as Uncovered in the saved source-ID coverage.
public static partial class StagedPartCoverage
{
    public const int MinProseWords = 5;

    // The part's candidate IDs, in source order.
    public static IReadOnlyList<string> Candidates(string partText)
    {
        var text = (partText ?? string.Empty).Replace("\r\n", "\n");
        var defining = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            if (IsTableOfContentsLine(line) || IsCrossReferenceRow(line)) continue;
            defining.UnionWith(SourceIdCoverage.Classify(line).Defined);
        }

        return SourceIdCoverage.Classify(text).Defined.Where(defining.Contains).ToList();
    }

    public static bool IsTableOfContentsLine(string line) => TableOfContentsTail().IsMatch(line);

    public static bool IsCrossReferenceRow(string line)
    {
        if (!line.Contains('|')) return false;
        var cells = line.Split('|').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        if (cells.Count < 2) return false;
        return cells.Count(IsIdsOnly) >= 2 && !cells.Any(c => Words().Matches(c).Count >= MinProseWords);
    }

    // A cell holding nothing but IDs, separated by commas, semicolons, slashes or spaces.
    private static bool IsIdsOnly(string cell) =>
        cell.Split([',', ';', '/', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .All(token => SrsIdExtractor.Extract(token) is [var id] && string.Equals(id, token, StringComparison.OrdinalIgnoreCase));

    // "FR-012" -> "FR", "TA-FR-3" -> "TA-FR", "REV-1.0" -> "REV".
    public static string Family(string id) => TrailingNumber().Replace(id.Trim(), string.Empty).ToUpperInvariant();

    // The coverage established by the generated requirements so far, against the full source text.
    public sealed class Ledger
    {
        private readonly SourceEvidenceIndex _index;
        private readonly HashSet<string> _covered = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _families = new(StringComparer.OrdinalIgnoreCase);

        public Ledger(string sourceText) => _index = SourceEvidenceIndex.Build(sourceText);

        public void Add(IEnumerable<StagedCoverageClaim> claims)
        {
            foreach (var claim in claims)
            {
                foreach (var id in claim.SourceIds.Where(id => !_covered.Contains(id)))
                {
                    if (claim.SourceQuotes.Any(q => _index.Check(q, id).Status == EvidenceStatus.Verified))
                    {
                        _covered.Add(id);
                        _families.Add(Family(id));
                    }
                }
            }
        }

        public bool IsCovered(string id) => _covered.Contains(id.Trim());

        public bool FamilyCovered(string id) => _families.Contains(Family(id));

        // Candidates of an empty part split into missing (fail) and unclassified (visible, not failed).
        public (IReadOnlyList<string> Missing, IReadOnlyList<string> Unclassified) Judge(IEnumerable<string> candidates)
        {
            var uncovered = candidates.Where(id => !IsCovered(id)).ToList();
            return (uncovered.Where(FamilyCovered).ToList(), uncovered.Where(id => !FamilyCovered(id)).ToList());
        }
    }

    [GeneratedRegex(@"(?:\.{3,}|(?:\.\s){3,}|…|\t)\s*\d{1,4}\s*$")]
    private static partial Regex TableOfContentsTail();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();

    [GeneratedRegex(@"-\d{1,5}(?:\.\d{1,4})*$")]
    private static partial Regex TrailingNumber();
}
