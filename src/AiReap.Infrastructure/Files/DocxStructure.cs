using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AiReap.Infrastructure.Files;

// The text of one Word document with its structure kept (see DocumentTextExtractor.DocxText for the
// line format). One instance reads one document once: numbering counters and note numbers advance as
// the body is read in order.
//
// Automatic numbering is worked out from the numbering and style parts as Word shows it for the usual
// cases (decimal, letters, roman numerals, level text such as "%1.%2" or "FR-%1:", start values and
// overrides, restart of lower levels, legal numbering). Counters are kept per abstract numbering, so lists
// that share one continue each other; fields such as SEQ or LISTNUM and numbering in headers or footers are
// not worked out.
internal sealed partial class DocxStructure
{
    public const string NotesHeading = "Headers, footers and notes (from the Word document):";

    private readonly MainDocumentPart _main;
    private readonly WordNumbering _numbering;
    private readonly Dictionary<long, int> _footnotes = [];
    private readonly Dictionary<long, int> _endnotes = [];
    private readonly List<string> _textBoxes = [];
    private int _pictures, _equations, _objects;

    public DocxStructure(MainDocumentPart main)
    {
        _main = main;
        _numbering = new WordNumbering(main);
    }

    public string Text(Body body)
    {
        var lines = new List<string>();
        Blocks(body.ChildElements, lines, numbered: true);

        var notes = new List<string>();
        AddDistinct(notes, "[Header]", _main.HeaderParts.Select(h => h.Header));
        AddDistinct(notes, "[Footer]", _main.FooterParts.Select(f => f.Footer));
        AddNotes(notes, "Footnote", _footnotes, _main.FootnotesPart?.Footnotes?.Elements<Footnote>().Select(f => (f.Id?.Value, (OpenXmlElement)f)));
        AddNotes(notes, "Endnote", _endnotes, _main.EndnotesPart?.Endnotes?.Elements<Endnote>().Select(e => (e.Id?.Value, (OpenXmlElement)e)));
        notes.AddRange(_textBoxes.Where(t => t.Length > 0).Select(t => $"[Text box] {t}"));

        var missing = new List<string>();
        if (_pictures > 0) missing.Add(Count(_pictures, "picture, chart or drawing", "pictures, charts or drawings"));
        if (_equations > 0) missing.Add(Count(_equations, "equation", "equations"));
        if (_objects > 0) missing.Add(Count(_objects, "embedded object or document", "embedded objects or documents"));
        if (missing.Count > 0)
        {
            notes.Add($"[Extraction note] Not in this text: {string.Join(", ", missing)}. Their content was not extracted.");
        }

        if (notes.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(NotesHeading);
            lines.AddRange(notes);
        }

        return string.Join("\n", lines);
    }

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    // The block-level content of a body, cell, header, note or content control, one line per paragraph
    // or table row.
    private void Blocks(IEnumerable<OpenXmlElement> elements, List<string> lines, bool numbered)
    {
        foreach (var element in elements)
        {
            switch (element)
            {
                case Paragraph paragraph:
                    lines.Add(ParagraphLine(paragraph, numbered));
                    break;
                case Table table:
                    foreach (var row in Rows(table))
                    {
                        lines.Add(RowLine(row, numbered));
                    }

                    break;
                case AltChunk:
                    _objects++;
                    break;
                case SdtBlock sdt:
                    Blocks(sdt.SdtContentBlock?.ChildElements ?? [], lines, numbered);
                    break;
                case CustomXmlBlock custom:
                    Blocks(custom.ChildElements, lines, numbered);
                    break;
            }
        }
    }

    private string ParagraphLine(Paragraph paragraph, bool numbered)
    {
        var text = VisibleText(paragraph, this);
        var label = numbered ? _numbering.Label(paragraph) : null;
        return label is null ? text : $"{label}\t{text}";
    }

    // The cells of a row joined by " | "; a "|" inside a cell is written "/" so cell boundaries stay
    // unambiguous. A table inside a cell is read into the cell, its rows joined by "; " and their cells
    // by ", ".
    private string RowLine(TableRow row, bool numbered)
    {
        var cells = row.Descendants<TableCell>().Where(c => c.Ancestors<TableRow>().First() == row).Select(cell =>
        {
            var parts = new List<string>();
            foreach (var element in cell.ChildElements)
            {
                if (element is Table nested)
                {
                    parts.Add(string.Join("; ", Rows(nested).Select(r => RowLine(r, numbered).Replace(" | ", ", "))));
                }
                else
                {
                    Blocks([element], parts, numbered);
                }
            }

            return Whitespace().Replace(string.Join(" ", parts.Where(p => p.Trim().Length > 0)), " ").Trim().Replace("|", "/");
        }).ToList();

        return cells.All(c => c.Length == 0) ? string.Empty : string.Join(" | ", cells);
    }

    // A table's own rows (also those inside content controls), not the rows of tables inside its cells.
    private static IEnumerable<TableRow> Rows(Table table) =>
        table.Descendants<TableRow>().Where(r => r.Ancestors<Table>().First() == table);

    // Each distinct non-empty line of the parts, once, labelled; a bare page number is left out.
    private void AddDistinct(List<string> notes, string label, IEnumerable<OpenXmlElement?> parts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.OfType<OpenXmlElement>())
        {
            var lines = new List<string>();
            Blocks(part.ChildElements, lines, numbered: false);
            foreach (var line in lines.Select(l => l.Trim()))
            {
                if (line.Length > 0 && !PageNumber().IsMatch(line) && seen.Add(Whitespace().Replace(line, " ")))
                {
                    notes.Add($"{label} {line}");
                }
            }
        }
    }

    // Notes referenced from the body, in the order they were referenced.
    private void AddNotes(List<string> notes, string label, Dictionary<long, int> numbers, IEnumerable<(long? Id, OpenXmlElement Note)>? all)
    {
        var byId = (all ?? []).Where(n => n.Id is not null).GroupBy(n => n.Id!.Value).ToDictionary(g => g.Key, g => g.First().Note);
        foreach (var (id, number) in numbers.OrderBy(n => n.Value))
        {
            if (!byId.TryGetValue(id, out var note))
            {
                continue;
            }

            var lines = new List<string>();
            Blocks(note.ChildElements, lines, numbered: false);
            var text = Whitespace().Replace(string.Join(" ", lines.Where(l => l.Trim().Length > 0)), " ").Trim();
            notes.Add($"[{label} {number}] {text}");
        }
    }

    // The visible text of a paragraph: run text, tabs and line breaks (as a space); not field
    // instructions (a table-of-contents entry reads "Actors and Roles\t9", which SourceReferenceIndex
    // recognises as a contents entry), deleted text, or the fallback copy of a drawing. With a reader,
    // note references become "[^n]" / "[^en]", text boxes are collected for the notes, and content that
    // is not text is counted for the extraction note.
    internal static string VisibleText(Paragraph paragraph, DocxStructure? reader = null)
    {
        var text = new StringBuilder();
        Visit(paragraph);
        return text.ToString();

        void Visit(OpenXmlElement element)
        {
            foreach (var child in element.ChildElements)
            {
                switch (child)
                {
                    case Text t:
                        text.Append(t.Text);
                        break;
                    case TabChar or PositionalTab:
                        text.Append('\t');
                        break;
                    case Break br:
                        if (br.Type is null || br.Type.Value == BreakValues.TextWrapping) text.Append(' ');
                        break;
                    case CarriageReturn:
                        text.Append(' ');
                        break;
                    case FootnoteReference f when reader is not null && f.Id?.Value is { } id:
                        text.Append($"[^{reader.NoteNumber(reader._footnotes, id)}]");
                        break;
                    case EndnoteReference e when reader is not null && e.Id?.Value is { } id:
                        text.Append($"[^e{reader.NoteNumber(reader._endnotes, id)}]");
                        break;
                    case AlternateContentFallback:
                        break;
                    case TextBoxContent box:
                        if (reader is not null)
                        {
                            var lines = new List<string>();
                            reader.Blocks(box.ChildElements, lines, numbered: false);
                            reader._textBoxes.Add(Whitespace().Replace(string.Join(" ", lines.Where(l => l.Trim().Length > 0)), " ").Trim());
                        }

                        break;
                    case Drawing or Picture:
                        if (reader is not null && !child.Descendants<TextBoxContent>().Any())
                        {
                            reader._pictures++;
                        }

                        Visit(child);
                        break;
                    case EmbeddedObject:
                        if (reader is not null) reader._objects++;
                        break;
                    default:
                        if (child.LocalName == "oMath" && reader is not null)
                        {
                            reader._equations++;
                            break;
                        }

                        Visit(child);
                        break;
                }
            }
        }
    }

    private int NoteNumber(Dictionary<long, int> numbers, long id)
    {
        if (!numbers.TryGetValue(id, out var n))
        {
            n = numbers.Count + 1;
            numbers[id] = n;
        }

        return n;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(?i:page\s*)?\d+(?:\s*(?:of|/)\s*\d+)?$")]
    private static partial Regex PageNumber();

    // Word automatic numbering (numbering part, paragraph styles).
    private sealed class WordNumbering
    {
        private readonly Dictionary<int, NumberingInstance> _instances = [];
        private readonly Dictionary<int, AbstractNum> _abstracts = [];
        private readonly Dictionary<string, Style> _styles = new(StringComparer.Ordinal);
        private readonly Dictionary<int, (int Value, bool Started)[]> _counters = [];
        private readonly HashSet<int> _overridden = [];

        public WordNumbering(MainDocumentPart main)
        {
            var numbering = main.NumberingDefinitionsPart?.Numbering;
            foreach (var instance in numbering?.Elements<NumberingInstance>() ?? [])
            {
                if (instance.NumberID?.Value is { } id) _instances[id] = instance;
            }

            foreach (var abstractNum in numbering?.Elements<AbstractNum>() ?? [])
            {
                if (abstractNum.AbstractNumberId?.Value is { } id) _abstracts[id] = abstractNum;
            }

            foreach (var style in main.StyleDefinitionsPart?.Styles?.Elements<Style>() ?? [])
            {
                if (style.StyleId?.Value is { } id) _styles.TryAdd(id, style);
            }
        }

        // The number Word shows before the paragraph, or null (none, a bullet, or not worked out).
        public string? Label(Paragraph paragraph)
        {
            var (numId, level) = Reference(paragraph);
            if (numId is not { } id || id == 0 || !_instances.TryGetValue(id, out var instance)
                || instance.AbstractNumId?.Val?.Value is not { } abstractId || Abstract(abstractId) is not { } abstractNum)
            {
                return null;
            }

            var ilvl = Math.Clamp(level ?? 0, 0, 8);
            var levels = Enumerable.Range(0, 9).Select(l => LevelOf(instance, abstractNum, l)).ToArray();
            if (levels[ilvl] is not { } current)
            {
                return null;
            }

            var key = abstractNum.AbstractNumberId!.Value;
            if (!_counters.TryGetValue(key, out var counters))
            {
                counters = new (int, bool)[9];
                _counters[key] = counters;
            }

            // A start override restarts the list the first time this instance is used.
            if (_overridden.Add(id))
            {
                foreach (var levelOverride in instance.Elements<LevelOverride>().Where(o => o.StartOverrideNumberingValue?.Val is not null))
                {
                    var l = levelOverride.LevelIndex?.Value ?? 0;
                    if (l is >= 0 and < 9) counters[l] = (levelOverride.StartOverrideNumberingValue!.Val!.Value - 1, true);
                }
            }

            counters[ilvl] = counters[ilvl].Started ? (counters[ilvl].Value + 1, true) : (Start(current), true);
            for (var deeper = ilvl + 1; deeper < 9; deeper++)
            {
                // lvlRestart 0: the deeper level never restarts; otherwise it restarts after a higher level.
                if (levels[deeper]?.LevelRestart?.Val?.Value is not 0) counters[deeper] = (0, false);
            }

            var format = current.NumberingFormat?.Val?.Value;
            if (format == NumberFormatValues.Bullet || format == NumberFormatValues.None)
            {
                return null;
            }

            var legal = current.IsLegalNumberingStyle is not null;
            var text = current.LevelText?.Val?.Value ?? string.Empty;
            var label = LevelPlaceholder().Replace(text, m =>
            {
                var l = int.Parse(m.Groups[1].Value) - 1;
                if (l is < 0 or > 8 || levels[l] is not { } at) return string.Empty;
                var value = counters[l].Started ? counters[l].Value : Start(at);
                return Format(value, legal && l < ilvl ? NumberFormatValues.Decimal : at.NumberingFormat?.Val?.Value);
            }).Trim();

            return label.Length == 0 ? null : label;
        }

        private static int Start(Level level) => level.StartNumberingValue?.Val?.Value ?? 0;

        // The paragraph's numbering, else its style's (following basedOn).
        private (int? NumId, int? Level) Reference(Paragraph paragraph)
        {
            var numPr = paragraph.ParagraphProperties?.NumberingProperties;
            int? numId = numPr?.NumberingId?.Val?.Value;
            int? level = numPr?.NumberingLevelReference?.Val?.Value;
            var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            for (var depth = 0; numId is null && styleId is not null && depth < 10 && _styles.TryGetValue(styleId, out var style); depth++)
            {
                var styleNum = style.StyleParagraphProperties?.NumberingProperties;
                numId = styleNum?.NumberingId?.Val?.Value;
                level ??= styleNum?.NumberingLevelReference?.Val?.Value;
                styleId = style.BasedOn?.Val?.Value;
            }

            return (numId, level);
        }

        // An abstract numbering, following a numbering style link once.
        private AbstractNum? Abstract(int id)
        {
            if (!_abstracts.TryGetValue(id, out var abstractNum)) return null;
            if (abstractNum.Elements<Level>().Any() || abstractNum.NumberingStyleLink?.Val?.Value is not { } link
                || !_styles.TryGetValue(link, out var style)
                || style.StyleParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value is not { } numId
                || !_instances.TryGetValue(numId, out var linked) || linked.AbstractNumId?.Val?.Value is not { } linkedId)
            {
                return abstractNum;
            }

            return _abstracts.GetValueOrDefault(linkedId) ?? abstractNum;
        }

        private static Level? LevelOf(NumberingInstance instance, AbstractNum abstractNum, int level) =>
            instance.Elements<LevelOverride>().FirstOrDefault(o => o.LevelIndex?.Value == level)?.Level
            ?? abstractNum.Elements<Level>().FirstOrDefault(l => (l.LevelIndex?.Value ?? 0) == level);

        private static string Format(int value, NumberFormatValues? format)
        {
            if (format == NumberFormatValues.LowerLetter) return Letters(value).ToLowerInvariant();
            if (format == NumberFormatValues.UpperLetter) return Letters(value);
            if (format == NumberFormatValues.LowerRoman) return Roman(value).ToLowerInvariant();
            if (format == NumberFormatValues.UpperRoman) return Roman(value);
            if (format == NumberFormatValues.DecimalZero) return value.ToString("00");
            return value.ToString();
        }

        // Word's letter numbering: a..z, then aa..zz, ...
        private static string Letters(int value)
        {
            if (value <= 0) return value.ToString();
            var letter = (char)('A' + (value - 1) % 26);
            return new string(letter, (value - 1) / 26 + 1);
        }

        private static string Roman(int value)
        {
            if (value is <= 0 or >= 4000) return value.ToString();
            var result = new StringBuilder();
            foreach (var (n, s) in new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
            {
                while (value >= n)
                {
                    result.Append(s);
                    value -= n;
                }
            }

            return result.ToString();
        }
    }

    [GeneratedRegex(@"%([1-9])")]
    private static partial Regex LevelPlaceholder();
}
