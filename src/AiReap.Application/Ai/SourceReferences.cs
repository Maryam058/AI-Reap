using System.Text;
using System.Text.RegularExpressions;
using AiReap.Application.Artifacts.Payloads;

namespace AiReap.Application.Ai;

// A reference the model claims for a clarification question's reason (RequirementAnalysis prompt).
// Never stored as given: SourceReferenceIndex.Resolve keeps it only where it resolves in the source.
public class ClaimedSourceReference
{
    public string? Label { get; set; }   // the words of the reason that refer to the source
    public string? Section { get; set; } // "3.2", "Section 3.2", "Appendix A"
    public string? Heading { get; set; } // "User Roles"
    public string? Quote { get; set; }   // verbatim words from the source
}

// One heading of a requirement source. Number: "3.2", "A" (after Appendix/Annex/...), or null for an
// unnumbered heading. Title: the heading without its number or table-of-contents tail. EndLine
// (exclusive) is where the next section that is not a subsection of this one starts.
public sealed record SourceSection(string? Number, string Title, string HeadingLine, int Line, int EndLine, bool TableOfContents);

// Deterministic resolution of the places a clarification question's reason points at in its
// requirement source (no AI call, no I/O). The source is the retained text (RequirementSource.RawText)
// the question was generated from - the original upload's pages and layout are not retained, so page
// references ("page 12") are never resolved. A reference is kept only when it resolves:
//   - "Section 3.2", "Sections 3.2 and 4.1", "§4", "Clause 7", "Appendix A" in the reason, where a
//     heading with that number is in the source (SourceChunker.IsSectionHeading). A heading in a table
//     of contents ("3.2 User Roles ..... 12") is used only when the section has no other heading;
//   - a quoted heading title ("the 'User Roles' section") equal to a heading's title;
//   - a reference the model claims (ClaimedSourceReference) whose label is in the reason, whose
//     section number or heading exists, and whose number and heading - when both are given - name
//     the same section. Its quote is kept only when it is found in the source, inside that section.
// Anything else - an unknown number, a heading that is not in the source, a page - stays plain text.
public sealed partial class SourceReferenceIndex
{
    private const int MaxExcerptChars = 300;
    private const int MinQuoteChars = 12;
    private const int MaxQuoteChars = 500;

    private readonly Guid _sourceId;
    private readonly string _text;
    private readonly int[] _lineStarts;
    private readonly string[] _lines;
    private readonly IReadOnlyList<SourceSection> _sections;

    // The normalized text quotes are searched in, and each of its characters' index in _text.
    private readonly string _normalized;
    private readonly List<int> _normalizedMap;

    private SourceReferenceIndex(Guid sourceId, string text)
    {
        _sourceId = sourceId;
        _text = text;
        _lines = Lines(text);
        _lineStarts = LineStarts(text, _lines);
        _sections = BuildSections(_lines);
        (_normalized, _normalizedMap) = Normalize(text);
    }

    public static SourceReferenceIndex Build(Guid sourceId, string? sourceText) => new(sourceId, sourceText ?? string.Empty);

    public IReadOnlyList<SourceSection> Sections => _sections;

    // The source's lines as the viewer splits them (web/src/lib/sourceReferences.ts).
    public static string[] Lines(string text) => LineBreak().Split(text);

    public List<SourceReferencePayload> Resolve(string? reason, IReadOnlyList<ClaimedSourceReference>? claimed = null)
    {
        var resolved = new List<SourceReferencePayload>();
        if (string.IsNullOrWhiteSpace(reason) || _sections.Count == 0 && (claimed is null || claimed.Count == 0))
        {
            return resolved;
        }

        // A claim that is in the reason but does not resolve blocks its words, so a contradicted
        // "Section 4 - Billing" (4 is Security) is not linked by the number alone below.
        var blocked = new List<(int Start, int End)>();
        bool Taken(int start, int end) => Overlaps(resolved, start, end) || blocked.Any(b => start < b.End && b.Start < end);

        foreach (var claim in claimed ?? [])
        {
            if (FromClaim(reason, claim, resolved, out var span) is { } reference)
            {
                resolved.Add(reference);
            }
            else if (span is { } words)
            {
                blocked.Add(words);
            }
        }

        foreach (Match match in KeywordReference().Matches(reason))
        {
            var numbers = match.Groups["n"].Captures;
            for (var i = 0; i < numbers.Count; i++)
            {
                // The first number's link includes its keyword ("Section 3.2"); later ones are the number alone.
                var start = i == 0 ? match.Index : numbers[i].Index;
                var end = numbers[i].Index + numbers[i].Length;
                if (Taken(start, end) || FindByNumber(numbers[i].Value) is not { } section)
                {
                    continue;
                }

                resolved.Add(ForSection(reason[start..end], start, section));
            }
        }

        foreach (Match match in QuotedTitle().Matches(reason))
        {
            var title = match.Groups["t"];
            if (!Taken(title.Index, title.Index + title.Length) && FindByTitle(title.Value) is { } section)
            {
                resolved.Add(ForSection(title.Value, title.Index, section));
            }
        }

        return resolved.OrderBy(r => r.Start).ToList();
    }

    // span: the claim's words in the reason, when they are there.
    private SourceReferencePayload? FromClaim(
        string reason, ClaimedSourceReference claim, List<SourceReferencePayload> taken, out (int Start, int End)? span)
    {
        span = null;
        var label = claim.Label?.Trim();
        if (string.IsNullOrEmpty(label))
        {
            return null;
        }

        var start = FreeIndexOf(reason, label, taken, StringComparison.Ordinal);
        if (start < 0)
        {
            start = FreeIndexOf(reason, label, taken, StringComparison.OrdinalIgnoreCase);
        }

        if (start < 0)
        {
            return null; // the label is not in the reason: there is nothing to link
        }

        label = reason.Substring(start, label.Length);
        span = (start, start + label.Length);
        SourceSection? section = null;
        if (!string.IsNullOrWhiteSpace(claim.Section))
        {
            section = FindByNumber(ClaimedNumber(claim.Section));
            if (section is null)
            {
                return null; // a section number that is not in the source
            }
        }

        if (!string.IsNullOrWhiteSpace(claim.Heading))
        {
            if (section is null)
            {
                section = FindByTitle(claim.Heading);
            }
            else if (!TitleMatches(section, claim.Heading))
            {
                return null; // number and heading name different sections: neither is trusted
            }
        }

        var quote = FindQuote(claim.Quote);
        if (quote is { } q && section is not null && (q.Line < section.Line || q.EndLine > section.EndLine))
        {
            quote = null; // found, but not in the section it was cited for
        }

        if (quote is { } found)
        {
            var containing = section ?? Containing(found.Line);
            return new SourceReferencePayload
            {
                Label = label,
                Start = start,
                SourceId = _sourceId,
                SectionNumber = containing?.Number,
                Heading = containing?.HeadingLine,
                Line = found.Line,
                EndLine = found.EndLine,
                Anchor = $"L{found.Line}",
                Excerpt = found.Text,
                ExcerptIsQuote = true
            };
        }

        return section is null ? null : ForSection(label, start, section);
    }

    // Where a verbatim quote sits in the source, with the section containing it; null when it is not
    // found as one continuous passage (FindQuote). Label is the quote as written in the source.
    public SourceReferencePayload? LocateQuote(string? quote)
    {
        if (FindQuote(quote) is not { } found)
        {
            return null;
        }

        var containing = Containing(found.Line);
        return new SourceReferencePayload
        {
            Label = found.Text,
            SourceId = _sourceId,
            SectionNumber = containing?.Number,
            Heading = containing?.HeadingLine,
            Line = found.Line,
            EndLine = found.EndLine,
            Anchor = $"L{found.Line}",
            Excerpt = found.Text,
            ExcerptIsQuote = true
        };
    }

    // The passage at character range [start, end) of the source, as a quote reference: its excerpt is the
    // source's own text there (surrounding whitespace dropped). Null for an empty or out-of-range span.
    public SourceReferencePayload? LocateRange(int start, int end)
    {
        start = Math.Clamp(start, 0, _text.Length);
        end = Math.Clamp(end, start, _text.Length);
        while (start < end && char.IsWhiteSpace(_text[start])) start++;
        while (end > start && char.IsWhiteSpace(_text[end - 1])) end--;
        if (start == end)
        {
            return null;
        }

        var line = LineOf(start);
        var containing = Containing(line);
        var text = _text[start..end];
        return new SourceReferencePayload
        {
            Label = text,
            SourceId = _sourceId,
            SectionNumber = containing?.Number,
            Heading = containing?.HeadingLine,
            Line = line,
            EndLine = LineOf(end - 1) + 1,
            Anchor = $"L{line}",
            Excerpt = text,
            ExcerptIsQuote = true
        };
    }

    // The innermost section (table of contents left out) a line is in.
    public SourceSection? SectionOf(int line) => Containing(line);

    // Every place a phrase is written in the source as whole words (compared as FindQuote compares), as
    // character ranges [Start, End). For short phrases - a term a question quotes - that the quote
    // check (SourceEvidenceIndex, four words at least) does not take.
    public IReadOnlyList<(int Start, int End)> PhraseOccurrences(string? phrase)
    {
        var (needle, _) = Normalize(phrase?.Trim() ?? string.Empty);
        needle = needle.Trim();
        var found = new List<(int Start, int End)>();
        if (needle.Length == 0)
        {
            return found;
        }

        bool WordChar(int at) => at >= 0 && at < _normalized.Length && char.IsLetterOrDigit(_normalized[at]);
        for (var at = _normalized.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = _normalized.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            if (!WordChar(at - 1) && !WordChar(at + needle.Length))
            {
                found.Add((_normalizedMap[at], _normalizedMap[at + needle.Length - 1] + 1));
            }
        }

        return found;
    }

    // The sentence of its line that holds character range [start, end): from the previous sentence end
    // (". ", "; ", "! ", "? ") or the line start, to the next one or the line end.
    public (int Start, int End) SentenceAround(int start, int end)
    {
        var line = LineOf(start);
        var lineStart = _lineStarts[line];
        var lineEnd = lineStart + _lines[line].Length;
        var from = start;
        while (from > lineStart && !(char.IsWhiteSpace(_text[from - 1]) && from - 2 >= lineStart && ".;!?".Contains(_text[from - 2])))
        {
            from--;
        }

        var to = Math.Min(Math.Max(end, start), lineEnd);
        while (to < lineEnd && !(".;!?".Contains(_text[to]) && (to + 1 == lineEnd || char.IsWhiteSpace(_text[to + 1]))))
        {
            to++;
        }

        return (from, Math.Min(to < lineEnd ? to + 1 : to, lineEnd));
    }

    // Whether a stored reference still holds in this source: a quote's excerpt is within its lines, a
    // section's heading is on its line. A reference to another source, or with no usable lines, does not.
    public bool Holds(SourceReferencePayload reference)
    {
        if (reference.SourceId != _sourceId || reference.Line < 0 || reference.EndLine <= reference.Line || reference.EndLine > _lines.Length)
        {
            return false;
        }

        if (reference.ExcerptIsQuote)
        {
            var (excerpt, _) = Normalize(reference.Excerpt ?? string.Empty);
            var (lines, _) = Normalize(string.Join("\n", _lines[reference.Line..reference.EndLine]));
            return excerpt.Trim().Length > 0 && lines.Contains(excerpt.Trim(), StringComparison.Ordinal);
        }

        return reference.Heading is { Length: > 0 } heading && _lines[reference.Line].Trim() == heading.Trim();
    }

    private SourceReferencePayload ForSection(string label, int start, SourceSection section) => new()
    {
        Label = label,
        Start = start,
        SourceId = _sourceId,
        SectionNumber = section.Number,
        Heading = section.HeadingLine,
        Line = section.Line,
        EndLine = section.EndLine,
        Anchor = $"L{section.Line}",
        Excerpt = SectionExcerpt(section)
    };

    // ---- sections --------------------------------------------------------------------------

    // A document with a table of contents whose entries are written again in its body (a Word file
    // whose heading styles were flattened to plain lines: "Background", "Special Case") takes its
    // headings from that table: a body line is a heading when it is one of the entries, and numbered
    // list items, colon lead-ins and capitalised notes in the body ("2. Does Axis want a confirmation
    // and error email?", "Fields:", "NOTE: ...") are not - they are text inside a section, so evidence
    // under them is labelled with the section it is really in. Markdown headings ("## 3.2") stay
    // headings. Without such a table (fewer than MinOutlineEntries entries, or fewer than
    // MinOutlineMatches of them found in the body) every SourceChunker.IsSectionHeading line counts.
    internal const int MinOutlineEntries = 3;
    internal const int MinOutlineMatches = 2;

    private static List<SourceSection> BuildSections(string[] lines)
    {
        var outline = Outline(lines);
        var headings = new List<(string? Number, string Title, string Line, int Index, bool Toc)>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (outline is not null)
            {
                var trimmedLine = lines[i].Trim();
                var entry = outline.Entries.Contains(i);
                // The Word extractor's own block for headers, footers and notes (DocxStructure.NotesHeading)
                // is a section of its own too.
                if (!entry && !outline.Titles.Contains(OutlineKey(trimmedLine)) && !MarkdownMarker().IsMatch(trimmedLine)
                    && !trimmedLine.StartsWith("Headers, footers and notes (", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!SourceChunker.IsSectionHeading(trimmedLine))
                {
                    var tocTitle = TableOfContentsTail().Replace(MarkdownMarker().Replace(trimmedLine, string.Empty), string.Empty).Trim();
                    headings.Add((null, tocTitle.TrimEnd(':').Trim(), trimmedLine, i, entry));
                    continue;
                }
            }
            else if (!SourceChunker.IsSectionHeading(lines[i]))
            {
                continue;
            }

            var trimmed = lines[i].Trim();
            var match = NumberedHeading().Match(trimmed);
            var number = match.Success ? match.Groups["num"].Value : null;
            var title = match.Success ? match.Groups["title"].Value : MarkdownMarker().Replace(trimmed, string.Empty);
            var toc = TableOfContentsTail().IsMatch(trimmed);
            title = TableOfContentsTail().Replace(title, string.Empty).Trim().TrimEnd(':').Trim();
            headings.Add((number, title, trimmed, i, toc));
        }

        var sections = new List<SourceSection>(headings.Count);
        for (var k = 0; k < headings.Count; k++)
        {
            var h = headings[k];
            var end = lines.Length;
            for (var j = k + 1; j < headings.Count; j++)
            {
                var next = headings[j];
                // A numbered section runs on through its subsections (3.2 -> 3.2.1) and ignores
                // unnumbered headings inside it ("Notes:"); an unnumbered one ends at the next heading.
                if (h.Number is null
                    || next.Number is not null && !next.Number.StartsWith(h.Number + ".", StringComparison.OrdinalIgnoreCase))
                {
                    end = next.Index;
                    break;
                }
            }

            while (end - 1 > h.Index && string.IsNullOrWhiteSpace(lines[end - 1]))
            {
                end--;
            }

            sections.Add(new SourceSection(h.Number, h.Title, h.Line, h.Index, end, h.Toc));
        }

        return sections;
    }

    private sealed record DocumentOutline(HashSet<int> Entries, HashSet<string> Titles);

    // The table of contents (see BuildSections), or null when there is none the body follows. Entries:
    // lines ending in a page number after a tab, dot leaders or a wide gap ("Special Case\t16"),
    // PAGEREF field text included. Titles: their keys (OutlineKey).
    private static DocumentOutline? Outline(string[] lines)
    {
        var entries = new HashSet<int>();
        var titles = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length is > 0 and <= 150 && TableOfContentsTail().IsMatch(trimmed) && OutlineKey(trimmed) is { Length: > 0 } key)
            {
                entries.Add(i);
                titles.Add(key);
            }
        }

        if (titles.Count < MinOutlineEntries)
        {
            return null;
        }

        var matched = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!entries.Contains(i) && OutlineKey(lines[i].Trim()) is { Length: > 0 } key && titles.Contains(key))
            {
                matched.Add(key);
            }
        }

        return matched.Count >= MinOutlineMatches ? new DocumentOutline(entries, titles) : null;
    }

    // A heading's title for comparing a body line with a table-of-contents entry: no Markdown marker,
    // page number or section number, case and spacing ignored.
    private static string OutlineKey(string line)
    {
        var text = TableOfContentsTail().Replace(MarkdownMarker().Replace(line, string.Empty), string.Empty).Trim();
        if (NumberedHeading().Match(text) is { Success: true } m && m.Groups["title"].Value.Trim().Length > 0)
        {
            text = m.Groups["title"].Value;
        }

        return text.Length > 120 ? string.Empty : TitleKey(text);
    }

    // The first heading with this number outside a table of contents, else the first in one.
    private SourceSection? FindByNumber(string number)
    {
        var key = number.Trim().TrimEnd('.');
        var candidates = _sections.Where(s => string.Equals(s.Number, key, StringComparison.OrdinalIgnoreCase)).ToList();
        return candidates.FirstOrDefault(s => !s.TableOfContents) ?? candidates.FirstOrDefault();
    }

    private SourceSection? FindByTitle(string title)
    {
        var key = TitleKey(title);
        if (key.Length == 0)
        {
            return null;
        }

        var candidates = _sections.Where(s => TitleKey(s.Title) == key).ToList();
        return candidates.FirstOrDefault(s => !s.TableOfContents) ?? candidates.FirstOrDefault();
    }

    // A claimed heading names the section when it is its title, or the title contains it (or the
    // other way round) - "User Roles" for "User Roles and Permissions".
    private static bool TitleMatches(SourceSection section, string heading)
    {
        var claimed = TitleKey(NumberedHeading().Match(heading.Trim()) is { Success: true } m ? m.Groups["title"].Value : heading);
        var actual = TitleKey(section.Title);
        return claimed.Length > 0 && actual.Length > 0 && (actual.Contains(claimed, StringComparison.Ordinal) || claimed.Contains(actual, StringComparison.Ordinal));
    }

    private static string TitleKey(string title) =>
        Whitespace().Replace(title.Trim().Trim('"', '\'', '\u201C', '\u201D', '\u2018', '\u2019').TrimEnd(':', '.'), " ").Trim().ToLowerInvariant();

    // "Section 3.2" / "§3.2" / "3.2." -> "3.2"; "Appendix A" -> "A".
    private static string ClaimedNumber(string section)
    {
        var match = NumberedHeading().Match(section.Trim() + " x");
        return match.Success ? match.Groups["num"].Value : section.Trim().TrimStart('§').Trim().TrimEnd('.');
    }

    private SourceSection? Containing(int line) =>
        _sections.Where(s => !s.TableOfContents && s.Line <= line && line < s.EndLine).MaxBy(s => s.Line);

    private string? SectionExcerpt(SourceSection section)
    {
        var body = new StringBuilder();
        for (var i = section.Line + 1; i < section.EndLine && body.Length < MaxExcerptChars; i++)
        {
            var line = _lines[i].Trim();
            if (line.Length > 0)
            {
                body.Append(body.Length > 0 ? " " : string.Empty).Append(line);
            }
        }

        if (body.Length == 0)
        {
            return null;
        }

        if (body.Length <= MaxExcerptChars)
        {
            return body.ToString();
        }

        var cut = body.ToString(0, MaxExcerptChars);
        var space = cut.LastIndexOf(' ');
        return (space > MaxExcerptChars / 2 ? cut[..space] : cut) + "\u2026";
    }

    // ---- quotes ----------------------------------------------------------------------------

    private (int Line, int EndLine, string Text)? FindQuote(string? quote)
    {
        var trimmed = quote?.Trim().Trim('"', '\u201C', '\u201D');
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length < MinQuoteChars || trimmed.Length > MaxQuoteChars
            || trimmed.Contains("...", StringComparison.Ordinal) || trimmed.Contains('\u2026'))
        {
            return null; // too short to locate reliably, too long, or not one continuous passage
        }

        var (needle, _) = Normalize(trimmed);
        needle = needle.Trim();
        var at = _normalized.IndexOf(needle, StringComparison.Ordinal);
        if (needle.Length == 0 || at < 0)
        {
            return null;
        }

        var start = _normalizedMap[at];
        var end = _normalizedMap[at + needle.Length - 1] + 1;
        return (LineOf(start), LineOf(end - 1) + 1, _text[start..end]);
    }

    // Lower-cased, whitespace runs as one space, curly quotes and dashes as their plain forms.
    private static (string Text, List<int> Map) Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] == ' ')
                {
                    continue;
                }

                c = ' ';
            }
            else
            {
                c = c switch
                {
                    '\u2018' or '\u2019' => '\'',
                    '\u201C' or '\u201D' => '"',
                    '\u2013' or '\u2014' or '\u2212' => '-',
                    _ => char.ToLowerInvariant(c)
                };
            }

            sb.Append(c);
            map.Add(i);
        }

        return (sb.ToString(), map);
    }

    private int LineOf(int offset)
    {
        var index = Array.BinarySearch(_lineStarts, offset);
        return index >= 0 ? index : ~index - 1;
    }

    private static int[] LineStarts(string text, string[] lines)
    {
        var starts = new int[lines.Length];
        var offset = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            starts[i] = offset;
            offset += lines[i].Length;
            if (offset < text.Length)
            {
                offset += text[offset] == '\r' && offset + 1 < text.Length && text[offset + 1] == '\n' ? 2 : 1;
            }
        }

        return starts;
    }

    // ---- labels ----------------------------------------------------------------------------

    private static int FreeIndexOf(string reason, string label, List<SourceReferencePayload> taken, StringComparison comparison)
    {
        for (var at = reason.IndexOf(label, comparison); at >= 0; at = reason.IndexOf(label, at + 1, comparison))
        {
            if (!Overlaps(taken, at, at + label.Length))
            {
                return at;
            }
        }

        return -1;
    }

    private static bool Overlaps(List<SourceReferencePayload> taken, int start, int end) =>
        taken.Any(r => start < r.Start + r.Label.Length && r.Start < end);

    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^#{1,6}\s+")]
    private static partial Regex MarkdownMarker();

    // "3.2 User Roles", "## 3.2. User Roles", "Section 4: Security", "Appendix A - Glossary".
    // A letter or roman numeral is a section number only after a keyword.
    [GeneratedRegex(@"^(?:#{1,6}\s+)?(?:(?i:section|chapter|part|appendix|annex|module|clause)\s+(?<num>\d+(?:\.\d+)*|[A-Z](?:\.\d+)*|[IVXLC]+)|§\s*(?<num>\d+(?:\.\d+)*)|(?<num>\d+(?:\.\d+)*))\.?(?=[\s:\-\u2013\u2014]|$)[\s:\-\u2013\u2014]*(?<title>.*)$")]
    private static partial Regex NumberedHeading();

    // A table-of-contents entry: a page number after dot leaders, a tab or a wide gap, or the field
    // instruction a Word contents entry carried in DOCX text extracted before those were dropped
    // ("Actors and RolesPAGEREF _Toc123 \h 9"). A heading that merely ends in a number
    // ("Release Phase 2") is not one.
    [GeneratedRegex(@"(?:(?:\s*(?:\.{3,}|\u2026+)\s*|\s{2,}|\t+)\d+|\s*PAGEREF\s+\S+(?:\s*\\[a-z])*\s*\d*)\s*$")]
    private static partial Regex TableOfContentsTail();

    // "Section 12.7", "Sections 3.2 and 4.1", "§4", "Appendix A", "clauses 3, 5 or 7".
    [GeneratedRegex(@"(?:\b(?i:sub-?sections?|sections?|sec\.|clauses?|chapters?|appendix|appendices|annex(?:es)?)|§§?)\s*(?<n>\d+(?:\.\d+)*(?![\w])|[A-Z](?:\.\d+)*(?![\w]))(?:\s*(?:,|\band\b|\bor\b|&|\bto\b|\bthrough\b|[\-\u2013\u2014])\s*(?<n>\d+(?:\.\d+)*(?![\w])))*")]
    private static partial Regex KeywordReference();

    // "the "User Roles" section", 'User Roles', curly quotes. Straight single quotes only around a
    // phrase (after a space or bracket, before a space or punctuation), so an apostrophe never opens one.
    [GeneratedRegex(@"[""\u201C](?<t>[^""\u201C\u201D\n]{3,80})[""\u201D]|\u2018(?<t>[^\u2018\u2019\n]{3,80})\u2019|(?<=^|[\s(])'(?<t>[^'\n]{3,80})'(?=[\s.,;:)!?]|$)")]
    private static partial Regex QuotedTitle();
}
