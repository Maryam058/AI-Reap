using System.Text.RegularExpressions;

namespace AiReap.Application.Ai.Pipeline;

// A piece of document context a rule inherits, copied verbatim from the source with its 0-based line.
//   heading     an enclosing heading (Level 1 = outermost); only headings that actually contain the rule
//               in the heading hierarchy, so a sibling subsection's heading is never inherited;
//   lead-in     the line ending in ":" that introduces the list or block the rule is in
//               ("When a referral is amended:"), and the lead-ins of enclosing list items;
//   scope       an earlier paragraph of the same section that says the rules after it apply under a
//               condition ("The following rules apply only when 'Is Careway' is enabled.");
//   table-title the caption line just above the rule's table ("Table 3: Bilateral processing");
//   table-header the rule's table header row;
//   definition  a sentence that defines a term the rule uses ("A referral is 'complex' when ...").
public sealed record InheritedContext(string Kind, string Text, int Line, int Level = 0);

// A table row read with its column headers. Conditions: the cells of columns that are not outcome
// columns, as "Header = cell"; Outcome: the cells of outcome columns ("Action", "Result", "Response"...).
// No outcome column: Condition is empty (which cells are conditions is unknown, so none is assumed).
public sealed record TableRowReading(string Reading, string Condition, string Outcome);

// Where every line of a source sits in its document structure (no AI call, no I/O), so a rule is read
// with the context it inherits (RuleRelations.Read). Only structure written in the source is used:
// heading hierarchy (Markdown "#" depth, else the depth of a section number "4.2.1", else top level),
// list lead-ins and list indentation, scope-setting sentences, table captions/headers and definitions.
// Context that is not written is unknown: nothing is inherited from a guess, and a lead-in separated
// from its block by a blank line, or a scope sentence in another section, is not attached.
public sealed partial class RuleContextIndex
{
    private readonly string[] _lines;
    private readonly List<(int Line, int Level, string Text)> _headings = [];
    private readonly List<(int Line, int End, string Text)> _scopes = [];
    private readonly Dictionary<int, (int Header, int? Title)> _tableRows = [];
    private readonly List<(string Term, int Line, string Text)> _definitions = [];

    private RuleContextIndex(string text)
    {
        _lines = SourceReferenceIndex.Lines(text);
        for (var i = 0; i < _lines.Length; i++)
        {
            if (HeadingLevel(_lines[i]) is { } level)
            {
                _headings.Add((i, level, _lines[i].Trim()));
            }
        }

        IndexTables();
        IndexScopesAndDefinitions();
    }

    public static RuleContextIndex Build(string? text) => new(text ?? string.Empty);

    public string[] Lines => _lines;

    // Everything the rule at this line inherits, outermost first.
    public IReadOnlyList<InheritedContext> ContextOf(int line)
    {
        var context = new List<InheritedContext>();
        if (line < 0 || line >= _lines.Length)
        {
            return context;
        }

        // Headings that contain the line: walking up, each must be strictly higher than the last taken.
        var chain = new List<(int Line, int Level, string Text)>();
        var threshold = int.MaxValue;
        foreach (var h in _headings.Where(h => h.Line < line).OrderByDescending(h => h.Line))
        {
            if (h.Level < threshold)
            {
                chain.Add(h);
                threshold = h.Level;
            }
        }

        chain.Reverse();
        context.AddRange(chain.Select(h => new InheritedContext("heading", h.Text, h.Line, h.Level)));
        context.AddRange(_scopes.Where(s => s.Line < line && line < s.End).Select(s => new InheritedContext("scope", s.Text, s.Line)));

        if (_tableRows.TryGetValue(line, out var table))
        {
            if (table.Title is { } title) context.Add(new InheritedContext("table-title", _lines[title].Trim(), title));
            context.Add(new InheritedContext("table-header", _lines[table.Header].Trim(), table.Header));
        }
        else
        {
            context.AddRange(LeadIns(line));
        }

        var words = _lines[line];
        foreach (var d in _definitions.Where(d => d.Line != line
                     && Regex.IsMatch(words, $@"\b{Regex.Escape(d.Term)}\b", RegexOptions.IgnoreCase)))
        {
            context.Add(new InheritedContext("definition", d.Text, d.Line));
        }

        return context;
    }

    // The row at this line read with its headers, or null when the line is not a table body row.
    public TableRowReading? RowAt(int line)
    {
        if (!_tableRows.TryGetValue(line, out var table))
        {
            return null;
        }

        var headers = Cells(_lines[table.Header]) ?? [];
        var cells = Cells(_lines[line]) ?? [];
        var pairs = cells.Select((c, k) => (Header: k < headers.Count ? headers[k] : string.Empty, Cell: c)).Where(p => p.Cell.Length > 0).ToList();
        var outcome = pairs.Where(p => OutcomeColumn().IsMatch(p.Header)).ToList();
        var reading = string.Join("; ", pairs.Select(p => p.Header.Length > 0 ? $"{p.Header}: {p.Cell}" : p.Cell));
        if (outcome.Count == 0)
        {
            return new TableRowReading(reading, string.Empty, reading);
        }

        // A cell holding a nested table (written "a, b; c, d" by the Word extraction) has no single value,
        // so it is no condition: which of its rows applies is unknown, never its first value.
        var condition = string.Join(" and ", pairs.Except(outcome).Where(p => p.Header.Length > 0 && !NestedTable(p.Cell)).Select(p => $"{p.Header} = {p.Cell}"));
        return new TableRowReading(reading, condition, string.Join("; ", outcome.Select(p => p.Cell)));
    }

    // A table inside a cell as DocxStructure writes it: two or more rows joined by "; ", each with the
    // same number (one or more) of ", " cell breaks.
    internal static bool NestedTable(string cell)
    {
        var rows = cell.Split("; ");
        if (rows.Length < 2)
        {
            return false;
        }

        var breaks = rows.Select(r => r.Split(", ").Length - 1).ToList();
        return breaks[0] >= 1 && breaks.All(b => b == breaks[0]);
    }

    // ---- headings ----------------------------------------------------------------------------------

    // A heading's level, or null for a line that is not one. A line ending in ":" is a lead-in here,
    // not a heading: it introduces the block under it rather than a section.
    private static int? HeadingLevel(string line)
    {
        var trimmed = line.Trim();
        if (!SourceChunker.IsSectionHeading(trimmed) || (trimmed.EndsWith(':') && !trimmed.StartsWith('#')))
        {
            return null;
        }

        if (Markdown().Match(trimmed) is { Success: true } md)
        {
            return md.Groups[1].Value.Length;
        }

        return Numbered().Match(trimmed) is { Success: true } num ? num.Groups["num"].Value.Split('.').Length : 1;
    }

    // ---- lead-ins ----------------------------------------------------------------------------------

    // Walking up through the rule's block: a lead-in applies when it is less indented than everything
    // between it and the rule (so a sibling list item's lead-in is not taken), or when it is the plain
    // paragraph that opens the list. A blank line ends the block unless list items continue across it.
    private List<InheritedContext> LeadIns(int line)
    {
        var found = new List<InheritedContext>();
        var indent = Indent(_lines[line]);
        var belowIsItem = ListItem().IsMatch(_lines[line]);
        for (var i = line - 1; i >= 0; i--)
        {
            var raw = _lines[i];
            if (raw.Trim().Length == 0)
            {
                var above = i - 1;
                while (above >= 0 && _lines[above].Trim().Length == 0) above--;
                if (!belowIsItem || above < 0 || !(ListItem().IsMatch(_lines[above]) || _lines[above].Trim().EndsWith(':')))
                {
                    break;
                }

                continue;
            }

            if (HeadingLevel(raw) is not null)
            {
                break;
            }

            var isItem = ListItem().IsMatch(raw);
            if (raw.Trim().EndsWith(':') && (Indent(raw) < indent || !isItem))
            {
                found.Add(new InheritedContext("lead-in", raw.Trim(), i));
                if (!isItem)
                {
                    break; // the paragraph that opens the list: nothing above it belongs to this block
                }
            }

            if (!isItem && !raw.Trim().EndsWith(':'))
            {
                break; // a plain paragraph: the block started below it
            }

            indent = Math.Min(indent, Indent(raw));
            belowIsItem = isItem;
        }

        found.Reverse();
        return found;
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    // ---- tables ------------------------------------------------------------------------------------

    private void IndexTables()
    {
        int? header = null;
        int? title = null;
        for (var i = 0; i < _lines.Length; i++)
        {
            var cells = Cells(_lines[i]);
            if (cells is null)
            {
                header = null;
                continue;
            }

            if (header is null)
            {
                if (i + 1 < _lines.Length && Cells(_lines[i + 1]) is not null)
                {
                    header = i;
                    title = Caption(i);
                }

                continue;
            }

            if (!cells.All(c => Regex.IsMatch(c, @"^:?-{3,}:?$")))
            {
                _tableRows[i] = (header.Value, title);
            }
        }
    }

    // The caption just above a table (one blank line allowed): "Table 3: ...", a line ending in ":", or a
    // bold title line ("**Bilateral processing**").
    private int? Caption(int header)
    {
        var i = header - 1;
        if (i >= 0 && _lines[i].Trim().Length == 0) i--;
        if (i < 0) return null;
        var line = _lines[i].Trim();
        return Cells(line) is null && line.Length > 0
            && (Regex.IsMatch(line, @"^(?i:table)\s+[\w.]+") || line.EndsWith(':') || Regex.IsMatch(line, @"^(\*\*|__)[^*_]{2,80}\1:?$")) ? i : null;
    }

    private static List<string>? Cells(string line)
    {
        if (!line.Contains('|')) return null;
        var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();
        return cells.Count >= 2 && cells.Count(c => c.Length > 0) >= 2 ? cells : null;
    }

    // ---- scope sentences and definitions --------------------------------------------------------------

    // A scope sentence covers the rest of the section it is written in (to the next heading at the same
    // or a higher level), up to the next scope sentence of that section.
    private void IndexScopesAndDefinitions()
    {
        var found = new List<(int Line, string Text, int SectionLevel)>();
        for (var i = 0; i < _lines.Length; i++)
        {
            var line = _lines[i].Trim();
            if (line.Length == 0 || HeadingLevel(line) is not null || Cells(line) is not null)
            {
                continue;
            }

            foreach (var sentence in Regex.Split(line, @"(?<=[.;!?])\s+").Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                if (ScopeSetter().IsMatch(sentence))
                {
                    var section = _headings.Where(h => h.Line < i).OrderByDescending(h => h.Line).Select(h => (int?)h.Level).FirstOrDefault() ?? 0;
                    found.Add((i, sentence, section));
                }

                if (Definition().Match(sentence) is { Success: true } d)
                {
                    _definitions.Add((d.Groups["term"].Value.Trim(), i, sentence));
                }
            }
        }

        foreach (var (line, text, level) in found)
        {
            var end = _headings.Where(h => h.Line > line && (level == 0 || h.Level <= level)).Select(h => h.Line).DefaultIfEmpty(_lines.Length).Min();
            var next = found.Where(f => f.Line > line && f.Line < end).Select(f => f.Line).DefaultIfEmpty(end).Min();
            _scopes.Add((line, next, text));
        }
    }

    [GeneratedRegex(@"^(#{1,6})\s+\S")]
    private static partial Regex Markdown();

    [GeneratedRegex(@"^(?<num>\d+(?:\.\d+)*)\.?\s+\S")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^\s*(?:\d+(?:\.\d+)*[.)]|[a-zA-Z][.)]|\([a-zA-Z0-9]+\)|[-*•+])\s+")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\b(?i:action|actions|outcome|result|response|behaviou?r|then|expected|handling|processing|system response|api call)\b")]
    private static partial Regex OutcomeColumn();

    // "The following rules apply only when ...", "The rules in this section apply to amendments.", and a
    // paragraph that introduces the workflow the section describes: "This section describes how amended
    // referrals are processed.", "Amendments to an existing referral are handled as follows." A plain
    // explanatory paragraph without such wording ("Referrals are amended by clinicians.") is not one:
    // nothing says the rules after it are limited to what it mentions.
    [GeneratedRegex(@"(?i:\b(?:the following|the rules below|the requirements below|these rules|the rules in this section|this section|in this section|the rest of this section)\b.*\bappl(?:y|ies)\b|\bappl(?:y|ies)\b.*\b(?:below|following)\b|^(?:this|the following) (?:section|subsection|chapter|workflow|process)\s+(?:describes|covers|defines|sets out|specifies|explains|outlines|details|deals with|relates to|concerns)\b|\b(?:are|is) (?:handled|processed|managed|performed|carried out|dealt with) as follows\b)")]
    private static partial Regex ScopeSetter();

    // "A referral is 'complex' when ...", "'Complex referral' means ...", "X is defined as ...".
    [GeneratedRegex(@"(?i:^(?:an?\s+|the\s+)?(?:[\p{L}]+\s+)?(?:is|are)\s+(?:considered\s+|deemed\s+|classed as\s+|treated as\s+)?['""‘“](?<term>[\p{L}][\p{L} -]{1,40})['""’”]\s+(?:when|if|where)\b|^['""‘“](?<term>[\p{L}][\p{L} -]{1,40})['""’”]\s+(?:means|refers to|is defined as)\b)")]
    private static partial Regex Definition();
}
