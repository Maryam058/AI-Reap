using System.Text.RegularExpressions;

namespace AiReap.Application.Ai;

// Requirement identifiers written in a requirement source ("FR-012", "REQ-3.2.1", "TA-FR-01"),
// found by a fixed pattern - never by AI - so requirements generated from the source can cite the
// IDs they come from and those citations can be checked against the text. A heuristic: IDs in
// other formats are simply not recognised (and so cannot be cited or linked). Order of first
// appearance is kept; comparison is case-insensitive.
public static partial class SrsIdExtractor
{
    public static IReadOnlyList<string> Extract(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new List<string>();
        foreach (Match m in RequirementId().Matches(text))
        {
            if (seen.Add(m.Value))
            {
                ids.Add(m.Value);
            }
        }

        return ids;
    }

    // The section heading (SourceChunker.IsSectionHeading) each ID first appears under; IDs before
    // the first heading are absent.
    public static IReadOnlyDictionary<string, string> SectionsById(string text)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (SourceChunker.IsSectionHeading(line))
            {
                current = line.Trim();
            }

            if (current is null)
            {
                continue;
            }

            foreach (Match m in RequirementId().Matches(line))
            {
                sections.TryAdd(m.Value, current);
            }
        }

        return sections;
    }

    // Letters/digits prefix (starting with a capital), optional further "-PART" segments, then a
    // number with optional ".n" levels: FR-012, REQ-3.2.1, TA-FR-01, NFR-7.
    [GeneratedRegex(@"\b[A-Z][A-Z0-9]{1,9}(?:-[A-Z0-9]{1,10})*-\d{1,5}(?:\.\d{1,4})*\b")]
    private static partial Regex RequirementId();
}

// The source IDs a response may cite: InPart, those in the text the model was given (a requirement's
// own sourceIds); InSource, those anywhere in the source (its dependsOnSourceIds may point to a
// requirement in another part).
public sealed record SourceIdScope(IReadOnlySet<string> InPart, IReadOnlySet<string> InSource)
{
    public static SourceIdScope For(string partText, string sourceText) => new(
        SrsIdExtractor.Extract(partText).ToHashSet(StringComparer.OrdinalIgnoreCase),
        SrsIdExtractor.Extract(sourceText).ToHashSet(StringComparer.OrdinalIgnoreCase));
}
