using System.Text;
using System.Text.RegularExpressions;

namespace AiReap.Application.Ai;

// Splits a requirement source that is too large for one AI request into parts that each fit
// maxChunkTokens (PromptBudgetReducer's estimate), without losing any of it: every non-blank line
// of the source lands, in order, in exactly one part. Boundaries are chosen from the most logical
// down: a section heading, then a paragraph / blank-line block, then a line (list items, numbered
// requirements), then a sentence, and only for a single sentence larger than a whole part, a word
// boundary. A part never ends on a bare heading, and when a new section starts in the second half
// of a full part, the part is closed before that heading so the section stays together.
public static partial class SourceChunker
{
    public static IReadOnlyList<string> Split(string text, int maxChunkTokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChunkTokens, 1);

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
        if (Tokens(normalized) <= maxChunkTokens)
        {
            return [normalized];
        }

        var units = new List<Unit>();
        foreach (var block in BlankLines().Split(normalized))
        {
            var trimmed = block.Trim('\n').TrimEnd();
            if (trimmed.Trim().Length == 0)
            {
                continue;
            }

            if (Tokens(trimmed) <= maxChunkTokens)
            {
                var startsSection = IsHeading(trimmed.Split('\n')[0]);
                units.Add(new Unit(trimmed, "\n\n", startsSection, startsSection && !trimmed.Contains('\n')));
                continue;
            }

            AddOversizedBlock(units, trimmed, maxChunkTokens);
        }

        return Pack(units, maxChunkTokens);
    }

    // Separator: what joined this unit to the previous one in the source (dropped at a part start).
    private sealed record Unit(string Text, string Separator, bool StartsSection, bool HeadingOnly);

    private static int Tokens(string text) => PromptBudgetReducer.EstimateTokens(text);

    // Per-unit estimate plus one token for its separator; the sum of per-unit estimates is never
    // below the estimate of the joined text, so a packed part is never over budget.
    private static int Cost(Unit unit) => Tokens(unit.Text) + 1;

    private static void AddOversizedBlock(List<Unit> units, string block, int max)
    {
        var separator = "\n\n";
        foreach (var line in block.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (Tokens(line) <= max)
            {
                var heading = IsHeading(line);
                units.Add(new Unit(line, separator, heading, heading));
            }
            else
            {
                var sentenceSeparator = separator;
                foreach (var sentence in SentenceEnd().Split(line))
                {
                    if (Tokens(sentence) <= max)
                    {
                        units.Add(new Unit(sentence, sentenceSeparator, false, false));
                    }
                    else
                    {
                        foreach (var piece in SplitAtWords(sentence, max))
                        {
                            units.Add(new Unit(piece, sentenceSeparator, false, false));
                            sentenceSeparator = " ";
                        }
                    }

                    sentenceSeparator = " ";
                }
            }

            separator = "\n";
        }
    }

    // Last resort for a single sentence larger than a part: whole words, or fixed slices of a
    // "word" that alone exceeds the budget (every character counts at most one token).
    private static IEnumerable<string> SplitAtWords(string sentence, int max)
    {
        var current = new StringBuilder();
        foreach (var word in sentence.Split(' '))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (Tokens(candidate) <= max)
            {
                current.Clear().Append(candidate);
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }

            var rest = word;
            while (Tokens(rest) > max)
            {
                yield return rest[..max];
                rest = rest[max..];
            }

            current.Append(rest);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static List<string> Pack(List<Unit> units, int max)
    {
        var parts = new List<string>();
        var current = new List<Unit>();
        var currentCost = 0;

        foreach (var unit in units)
        {
            while (current.Count > 0 && currentCost + Cost(unit) > max)
            {
                var cut = CutIndex(current, max);
                parts.Add(Render(current.GetRange(0, cut)));
                current.RemoveRange(0, cut);
                currentCost = current.Sum(Cost);
            }

            current.Add(unit);
            currentCost += Cost(unit);
        }

        if (current.Count > 0)
        {
            parts.Add(Render(current));
        }

        return parts;
    }

    // Where to close a full part: before the last section heading that starts in its second half,
    // else before a trailing bare heading, else after its last unit. Always >= 1, so packing advances.
    private static int CutIndex(List<Unit> current, int max)
    {
        var prefix = Cost(current[0]);
        var cut = current.Count;
        for (var k = 1; k < current.Count; k++)
        {
            if (current[k].StartsSection && prefix >= max / 2)
            {
                cut = k;
            }

            prefix += Cost(current[k]);
        }

        if (cut == current.Count && current.Count > 1 && current[^1].HeadingOnly)
        {
            cut = current.Count - 1;
        }

        return cut;
    }

    private static string Render(List<Unit> units)
    {
        var sb = new StringBuilder(units[0].Text);
        foreach (var unit in units.Skip(1))
        {
            sb.Append(unit.Separator).Append(unit.Text);
        }

        return sb.ToString();
    }

    private static bool IsHeading(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length is 0 or > 100)
        {
            return false;
        }

        if (MarkdownHeading().IsMatch(trimmed) || NamedSection().IsMatch(trimmed))
        {
            return true;
        }

        // "3.2 Leave approval" - but not a numbered requirement sentence "3. Users must log in."
        if (NumberedHeading().IsMatch(trimmed) && !trimmed.EndsWith('.'))
        {
            return true;
        }

        // "LEAVE APPROVAL" or "Functional requirements:"
        var letters = trimmed.Count(char.IsLetter);
        if (letters >= 3 && trimmed.Length <= 80 && !trimmed.Any(char.IsLower))
        {
            return true;
        }

        return trimmed.EndsWith(':') && !trimmed.StartsWith('-') && !trimmed.StartsWith('*');
    }

    [GeneratedRegex(@"\n[ \t]*\n(?:[ \t]*\n)*")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"(?<=[.!?;])\s+")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"^#{1,6}\s+\S")]
    private static partial Regex MarkdownHeading();

    [GeneratedRegex(@"^(?:section|chapter|part|appendix|module)\s+[0-9IVXLA-Z]+\b", RegexOptions.IgnoreCase)]
    private static partial Regex NamedSection();

    [GeneratedRegex(@"^\d+(?:\.\d+)*\.?\s+\S")]
    private static partial Regex NumberedHeading();
}
