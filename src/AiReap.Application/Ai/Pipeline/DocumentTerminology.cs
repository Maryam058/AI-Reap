using System.Text.RegularExpressions;

namespace AiReap.Application.Ai.Pipeline;

// Terms a document itself says are the same thing (no AI call, no I/O). Two words are taken as one only
// when the source states it in words or in a glossary table:
//   "Patient (also referred to as Client)", "a patient, also known as a client",
//   "Patients are also called clients", "'Client' and 'patient' are used interchangeably",
//   "'Client' means the patient." (a one-word definition), or a table row under a header such as
//   "Term | Synonym" / "Term | Also known as" / "Term | Alias".
// Nothing is inferred from words merely appearing together: without such a statement "patient" and
// "client" stay different terms. Only single-term names are linked (each side must be one term after
// ClarificationCandidateValidator.Terms); multi-word names are left as written.
public sealed partial class TermAliases
{
    public static readonly TermAliases None = new([], []);

    private readonly Dictionary<string, string> _canonical;

    private TermAliases(Dictionary<string, string> canonical, List<TermAlias> pairs)
    {
        _canonical = canonical;
        Pairs = pairs;
    }

    // Each statement found, with its 0-based source line, for traceability.
    public IReadOnlyList<TermAlias> Pairs { get; }

    public string Canon(string term) => _canonical.GetValueOrDefault(term, term);

    public HashSet<string> Canon(HashSet<string> terms) =>
        _canonical.Count == 0 ? terms : terms.Select(Canon).ToHashSet(StringComparer.Ordinal);

    public static TermAliases From(string? text)
    {
        var lines = SourceReferenceIndex.Lines(text ?? string.Empty);
        var pairs = new List<TermAlias>();
        List<string>? aliasHeader = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (Cells(line) is { } cells)
            {
                if (aliasHeader is null && cells.Count >= 2 && AliasColumn().IsMatch(string.Join(" ", cells.Skip(1))))
                {
                    aliasHeader = cells;
                    continue;
                }

                if (aliasHeader is not null && !cells.All(c => Regex.IsMatch(c, @"^:?-{3,}:?$")))
                {
                    for (var k = 1; k < Math.Min(cells.Count, aliasHeader.Count); k++)
                    {
                        if (AliasColumn().IsMatch(aliasHeader[k]))
                        {
                            foreach (var alias in cells[k].Split([',', ';', '/'], StringSplitOptions.RemoveEmptyEntries))
                            {
                                Add(pairs, cells[0], alias, i, line);
                            }
                        }
                    }
                }

                continue;
            }

            aliasHeader = null;
            foreach (Match m in Statement().Matches(line))
            {
                Add(pairs, m.Groups["a"].Value, m.Groups["b"].Value, i, line);
            }

            if (OneWordDefinition().Match(line) is { Success: true } d)
            {
                Add(pairs, d.Groups["a"].Value, d.Groups["b"].Value, i, line);
            }
        }

        if (pairs.Count == 0)
        {
            return None;
        }

        // Union-find over the linked terms; the alphabetically first term of each group is its name.
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string x)
        {
            while (parent.TryGetValue(x, out var p) && p != x) x = p;
            return x;
        }

        foreach (var p in pairs)
        {
            parent.TryAdd(p.First, p.First);
            parent.TryAdd(p.Second, p.Second);
            var (ra, rb) = (Find(p.First), Find(p.Second));
            if (ra != rb)
            {
                if (string.CompareOrdinal(ra, rb) < 0) parent[rb] = ra; else parent[ra] = rb;
            }
        }

        return new TermAliases(parent.Keys.ToDictionary(k => k, Find, StringComparer.Ordinal), pairs);
    }

    private static void Add(List<TermAlias> pairs, string a, string b, int line, string text)
    {
        var ta = ClarificationCandidateValidator.Terms(a);
        var tb = ClarificationCandidateValidator.Terms(b);
        if (ta.Count == 1 && tb.Count == 1 && ta.First() != tb.First() && !ta.First().Any(char.IsDigit) && !tb.First().Any(char.IsDigit))
        {
            pairs.Add(new TermAlias(ta.First(), tb.First(), line, text));
        }
    }

    private static List<string>? Cells(string line)
    {
        if (!line.Contains('|')) return null;
        var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToList();
        return cells.Count >= 2 && cells.Count(c => c.Length > 0) >= 2 ? cells : null;
    }

    // "X (also known as / also referred to as / hereafter / aka Y)", "X, also known as Y",
    // "X is/are also called / known as / referred to as Y", "X and Y are used interchangeably".
    [GeneratedRegex(@"(?i:(?<a>\b[\p{L}][\p{L}-]*)['""’”]?\s*[(,]\s*(?:(?:(?:also|hereafter|hereinafter)\s+)?(?:known as|referred to as)|(?:also|hereafter|hereinafter)\s+called|aka|a\.k\.a\.)\s+['""‘“]?(?:an?\s+|the\s+)?(?<b>[\p{L}][\p{L}-]*)|(?<a>\b[\p{L}][\p{L}-]*)['""’”]?\s+(?:is|are)\s+(?:also\s+(?:known as|referred to as|called)|known as|referred to as)\s+['""‘“]?(?:an?\s+|the\s+)?(?<b>[\p{L}][\p{L}-]*)|['""‘“]?(?<a>\b[\p{L}][\p{L}-]*)['""’”]?\s+and\s+['""‘“]?(?<b>[\p{L}][\p{L}-]*)['""’”]?\s+are\s+used\s+interchangeably)")]
    private static partial Regex Statement();

    // "'Client' means the patient." - a definition that is one word, nothing more.
    [GeneratedRegex(@"(?i:^['""‘“]?(?<a>[\p{L}][\p{L}-]*)['""’”]?\s+means\s+(?:an?\s+|the\s+)?['""‘“]?(?<b>[\p{L}][\p{L}-]*)['""’”]?\s*\.?$)")]
    private static partial Regex OneWordDefinition();

    [GeneratedRegex(@"(?i:\b(?:synonyms?|aliases?|also known as|aka|alternative (?:terms?|names?)|other names?|equivalent terms?)\b)")]
    private static partial Regex AliasColumn();
}

public sealed record TermAlias(string First, string Second, int Line, string Text);
