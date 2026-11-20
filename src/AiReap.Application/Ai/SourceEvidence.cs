using System.Text;
using System.Text.RegularExpressions;

namespace AiReap.Application.Ai;

// Deterministic quote checking against a requirement source (no AI call, no I/O). It answers one
// narrow question: are the quoted words actually written in the requirement source text - and, when
// a requirement ID is cited, in that ID's passage? It does NOT decide whether a requirement follows
// from its quote, nor whether the requirement is correct: a real quote can sit next to an
// overreaching requirement. "Verified" therefore means only "quote found in the source".
//
// Matching. Quote and source are normalized the same way (Tokenize) and compared word for word:
//   - ignored: case, Unicode compatibility forms (PDF ligatures), curly quotes and dash variants,
//     soft hyphens and zero-width characters, whitespace and line breaks, table separators,
//     bullets, punctuation that is not part of a number, a hyphen between two letters ("e-mail" =
//     "email") and a word hyphenated across a line break ("authen-\ntication");
//   - kept exactly: every word, every number (with its decimal point, thousands separator or
//     hyphen: "1.5", "1,000", "-5", "FR-012"), comparison and unit symbols (% / < > = <= >=), and
//     negations - so a changed number, unit or "not" never matches.
// An ellipsis quote ("A ... B") matches only where the source text it skips contains no negation,
// exclusivity, exception, condition or bound (see SourceEvidenceIndex.OmitsMaterialTerm); a quote
// whose fragments are found only around such a term is Altered. This is a lexical check, not an
// understanding of meaning: other omissions can still change what a quote says.
// A quote is accepted strictly first; only if that fails is EvidenceMatchMode.SpacingTolerant tried,
// for PDFs whose extraction merged words ("Thesystemshall"). That mode accepts a match only where the
// source has merged words the quote spells apart, every source word boundary is also a quote word
// boundary, each merged source word is long enough to be an extraction artifact rather than a
// different word ("island" is never "is land"), and no merge joins two digits ("15" is never "1 5").
// Similarity is used only to tell an altered quote from an invented one; it never verifies anything.
//
// Limitations: only the requirement source text given to Build is searched - a quote taken from a
// clarification answer or a project document is reported NotFound. Requirement IDs are those
// SrsIdExtractor recognises; an ID in another format is reported UnknownSourceId.
public enum EvidenceStatus
{
    // No quote was given: nothing to check.
    None,
    // Every quote given was found in the source (at the cited ID's passage, when one is cited).
    Verified,
    // A quote was given but could not be found as written; see EvidenceReason.
    Unverified
}

public enum EvidenceReason
{
    // Not in the source, and not close to anything in it.
    NotFound,
    // Not in the source as written, but most of its words appear together somewhere: a paraphrase,
    // a changed number or unit, a dropped negation, reordered words, or an ellipsis that skips a
    // negation or qualifier. A hint for reviewers only.
    Altered,
    // Fewer than SourceEvidence.MinQuoteWords words (or an ellipsis fragment under MinFragmentWords):
    // too little text to show where a requirement comes from.
    TooShort,
    // The cited requirement ID is not written in the source.
    UnknownSourceId,
    // Found in the source, but not in the cited requirement ID's passage.
    NotAtCitedId,
    // The source text is empty or unavailable, so nothing can be verified.
    SourceUnavailable
}

public enum EvidenceMatchMode
{
    None,
    Strict,
    SpacingTolerant
}

// The outcome for one quote. MatchCount: how often the quote occurs in the source (in the mode that
// matched), whether or not inside the cited ID's passage.
public sealed record QuoteCheck(
    string? Quote, string? SourceId, EvidenceStatus Status, EvidenceReason? Reason, EvidenceMatchMode MatchMode, int MatchCount);

// The outcome for one item's quotes: None when no quote was given, Verified only when every quote
// given is verified, otherwise Unverified. Every quote's own result is kept.
public sealed record EvidenceAssessment(EvidenceStatus Status, IReadOnlyList<QuoteCheck> Quotes);

public static partial class SourceEvidence
{
    public const int MinQuoteWords = 4;
    public const int MinFragmentWords = 2;

    // Share of a quote's words that must appear together in one source window for an unmatched quote
    // to be reported Altered rather than NotFound.
    public const double AlteredOverlapThreshold = 0.70;

    // Most characters of source allowed between consecutive fragments of an ellipsis quote ("A ... B").
    public const int MaxEllipsisGapChars = 600;

    // SpacingTolerant: a source word that absorbs several quote words is accepted as merged by
    // extraction only when it is at least this long, or absorbs at least MinMergedTokenWords words.
    public const int MinMergedTokenLength = 10;
    public const int MinMergedTokenWords = 3;

    // The comparison form of a text: its tokens joined by single spaces.
    public static string Normalize(string? text) => string.Join(' ', Tokenize(text ?? string.Empty).Select(t => t.Text));

    // The numbers (with their unit, when one follows) in a claim - a requirement's expected result,
    // an NFR target - that none of the given quotes contains, in the claim's own wording. A real quote
    // does not show that a requirement's figures came from the source; this does, deterministically.
    // Requirement IDs ("FR-012") are not numbers. A unit is compared only when the claim's number is
    // directly followed by one of a fixed set of units (time, size, percentage, counts).
    public static IReadOnlyList<string> NumbersMissingFromEvidence(string? claim, IEnumerable<string?> verifiedQuotes)
    {
        if (string.IsNullOrWhiteSpace(claim))
        {
            return [];
        }

        var quotes = verifiedQuotes.Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => Tokenize(q!)).ToList();
        var tokens = Tokenize(claim);
        var missing = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!NumberToken().IsMatch(tokens[i].Text))
            {
                continue;
            }

            var unit = i + 1 < tokens.Count ? Unit(tokens[i + 1].Text) : null;
            var number = tokens[i].Text;
            var found = quotes.Any(q => Enumerable.Range(0, q.Count).Any(k =>
                q[k].Text == number && (unit is null || (k + 1 < q.Count && Unit(q[k + 1].Text) == unit))));
            if (found)
            {
                continue;
            }

            var end = unit is null ? tokens[i].End : tokens[i + 1].End;
            var text = claim[tokens[i].Start..end];
            if (!missing.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                missing.Add(text);
            }
        }

        return missing;
    }

    // A normalized word, number or symbol, with its [Start, End) character range in the original text.
    internal readonly record struct Token(string Text, int Start, int End)
    {
        public bool IsWord => Text.Any(char.IsLetterOrDigit);
    }

    // The fragments of a quote written with an ellipsis ("A ... B"), each tokenized; empty ones dropped.
    internal static List<List<Token>> Fragments(string quote) =>
        Ellipsis().Split(quote).Select(Tokenize).Where(f => f.Count > 0).ToList();

    internal static List<Token> Tokenize(string text)
    {
        var (chars, map) = Prepare(text);
        var tokens = new List<Token>();
        var current = new StringBuilder();
        var start = -1;
        var last = -1;

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(new Token(current.ToString(), map[start], map[last] + 1));
                current.Clear();
            }
        }

        void Append(char c, int index)
        {
            if (current.Length == 0) start = index;
            current.Append(c);
            last = index;
        }

        for (var j = 0; j < chars.Count; j++)
        {
            var c = chars[j];
            if (char.IsLetterOrDigit(c))
            {
                Append(c, j);
                continue;
            }

            var prev = current.Length > 0 ? current[^1] : '\0';
            var next = j + 1 < chars.Count ? chars[j + 1] : '\0';
            switch (c)
            {
                case Hyphen when char.IsLetter(prev) && LineBreakContinuation(chars, j + 1) is { } resume:
                    j = resume - 1; // a word hyphenated across a line break: joined
                    continue;
                case Hyphen when char.IsLetter(prev) && char.IsLetter(next):
                    continue; // "e-mail" = "email"
                case Hyphen or Dash when char.IsDigit(prev) || char.IsDigit(next):
                    Append(Hyphen, j); // "FR-012", "-5", "5-10", "2026-03-01"
                    continue;
                case '.' or ',' when char.IsDigit(prev) && char.IsDigit(next):
                    Append(c, j); // "1.5", "1,000", "3.2.1"
                    continue;
                case '\'' when char.IsLetter(prev) && char.IsLetter(next):
                    continue; // "can't" = "cant"
            }

            Flush();
            if (Symbols.Contains(c))
            {
                Append(c, j);
                Flush();
            }
        }

        Flush();
        return tokens;
    }

    private const char Hyphen = '-';

    // Internal stand-in for en/em dashes and minus signs: a separator between words, a hyphen next
    // to a digit.
    private const char Dash = '–';

    private static readonly HashSet<char> Symbols = ['%', '/', '<', '>', '=', '≤', '≥', '+', '$', '€', '£', '°', '&', '@', '#'];

    // After a hyphen: optional spaces, a line break, optional spaces, then a letter -> that letter's index.
    private static int? LineBreakContinuation(List<char> chars, int index)
    {
        var sawBreak = false;
        for (var k = index; k < chars.Count; k++)
        {
            var c = chars[k];
            if (c == '\n') sawBreak = true;
            else if (c is ' ' or '\t' or '\r') continue;
            else return sawBreak && char.IsLetter(c) ? k : null;
        }

        return null;
    }

    // Lower-cased, compatibility-normalized characters, each mapped back to its index in the original.
    private static (List<char> Chars, List<int> Map) Prepare(string text)
    {
        var chars = new List<char>(text.Length);
        var map = new List<int>(text.Length);

        void Add(string s, int index)
        {
            foreach (var ch in s)
            {
                chars.Add(char.ToLowerInvariant(ch));
                map.Add(index);
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '­' or '​' or '‌' or '‍' or '⁠' or '﻿':
                    continue;
                case '‘' or '’' or '‚' or '‛' or '′':
                    Add("'", i);
                    continue;
                case '“' or '”' or '„' or '‟' or '″':
                    Add("\"", i);
                    continue;
                case '‐' or '‑' or '－':
                    Add("-", i);
                    continue;
                case '‒' or '–' or '—' or '―' or '−' or '﹣':
                    Add(Dash.ToString(), i);
                    continue;
                case '…':
                    Add("...", i);
                    continue;
                case ' ' or ' ' or ' ':
                    Add(" ", i);
                    continue;
            }

            if (c < '\u0080')
            {
                Add(c.ToString(), i);
                continue;
            }

            var length = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            string normalized;
            try
            {
                normalized = text.Substring(i, length).Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException)
            {
                normalized = " "; // a lone surrogate: treated as a separator
            }

            Add(normalized, i);
            i += length - 1;
        }

        return (chars, map);
    }

    private static string? Unit(string token) => Units.TryGetValue(token, out var unit) ? unit : null;

    private static readonly Dictionary<string, string> Units = new(StringComparer.Ordinal)
    {
        ["ms"] = "ms", ["millisecond"] = "ms", ["milliseconds"] = "ms",
        ["s"] = "second", ["sec"] = "second", ["secs"] = "second", ["second"] = "second", ["seconds"] = "second",
        ["min"] = "minute", ["mins"] = "minute", ["minute"] = "minute", ["minutes"] = "minute",
        ["h"] = "hour", ["hr"] = "hour", ["hrs"] = "hour", ["hour"] = "hour", ["hours"] = "hour",
        ["day"] = "day", ["days"] = "day", ["week"] = "week", ["weeks"] = "week",
        ["month"] = "month", ["months"] = "month", ["year"] = "year", ["years"] = "year",
        ["%"] = "%", ["percent"] = "%",
        ["kb"] = "kb", ["mb"] = "mb", ["gb"] = "gb", ["tb"] = "tb",
        ["user"] = "user", ["users"] = "user", ["request"] = "request", ["requests"] = "request",
        ["attempt"] = "attempt", ["attempts"] = "attempt", ["character"] = "character", ["characters"] = "character",
        ["rps"] = "rps", ["tps"] = "tps"
    };

    [GeneratedRegex(@"^-?\d[\d.,-]*$")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"\.{3,}|…|(?:\.\s){2,}\.")]
    private static partial Regex Ellipsis();
}

// One requirement source, prepared once for checking any number of quotes: its tokens, an index of
// where each token occurs, the words-joined form used by SpacingTolerant, and the passage of every
// requirement ID. Immutable after Build, so it can be shared.
public sealed partial class SourceEvidenceIndex
{
    private readonly List<SourceEvidence.Token> _tokens;
    private readonly Dictionary<string, List<int>> _positions = new(StringComparer.Ordinal);
    private readonly string _joined;
    private readonly int[] _joinedStarts;
    private readonly Dictionary<string, List<(int Start, int End)>> _passagesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(int Start, int End)>> _definitionsById = new(StringComparer.OrdinalIgnoreCase);

    private SourceEvidenceIndex(string text)
    {
        _tokens = SourceEvidence.Tokenize(text);
        _joinedStarts = new int[_tokens.Count];
        var joined = new StringBuilder();
        for (var i = 0; i < _tokens.Count; i++)
        {
            if (!_positions.TryGetValue(_tokens[i].Text, out var list)) _positions[_tokens[i].Text] = list = [];
            list.Add(i);
            _joinedStarts[i] = joined.Length;
            joined.Append(_tokens[i].Text);
        }

        _joined = joined.ToString();
        IndexPassages(text);
    }

    public static SourceEvidenceIndex Build(string? sourceText) => new(sourceText ?? string.Empty);

    public bool HasSource => _tokens.Count > 0;

    public QuoteCheck Check(string? quote, string? sourceId = null)
    {
        if (string.IsNullOrWhiteSpace(quote))
        {
            return new QuoteCheck(quote, sourceId, EvidenceStatus.None, null, EvidenceMatchMode.None, 0);
        }

        QuoteCheck Unverified(EvidenceReason reason, EvidenceMatchMode mode = EvidenceMatchMode.None, int count = 0) =>
            new(quote, sourceId, EvidenceStatus.Unverified, reason, mode, count);

        if (!HasSource)
        {
            return Unverified(EvidenceReason.SourceUnavailable);
        }

        var fragments = SourceEvidence.Fragments(quote);
        if (fragments.Sum(f => f.Count(t => t.IsWord)) < SourceEvidence.MinQuoteWords
            || (fragments.Count > 1 && fragments.Any(f => f.Count(t => t.IsWord) < SourceEvidence.MinFragmentWords)))
        {
            return Unverified(EvidenceReason.TooShort);
        }

        List<(int Start, int End)>? passages = null;
        if (!string.IsNullOrWhiteSpace(sourceId) && !_passagesById.TryGetValue(sourceId.Trim(), out passages))
        {
            return Unverified(EvidenceReason.UnknownSourceId);
        }

        var mode = EvidenceMatchMode.Strict;
        var (matches, omitsMaterial) = Find(fragments, tolerant: false);
        if (matches.Count == 0)
        {
            mode = EvidenceMatchMode.SpacingTolerant;
            (matches, var tolerantOmitsMaterial) = Find(fragments, tolerant: true);
            omitsMaterial |= tolerantOmitsMaterial;
        }

        if (matches.Count == 0)
        {
            // The fragments are in the source, but only with a negation or qualifier between them.
            return Unverified(omitsMaterial || BestOverlap(fragments.SelectMany(f => f).ToList()) >= SourceEvidence.AlteredOverlapThreshold
                ? EvidenceReason.Altered
                : EvidenceReason.NotFound);
        }

        if (passages is not null && !matches.Any(m => passages.Any(p =>
                _tokens[m.Start].Start >= p.Start && _tokens[m.End - 1].End <= p.End)))
        {
            return Unverified(EvidenceReason.NotAtCitedId, mode, matches.Count);
        }

        return new QuoteCheck(quote, sourceId, EvidenceStatus.Verified, null, mode, matches.Count);
    }

    // Where a quote that Check verifies is written: the character range [Start, End) in the source text
    // of each match, in source order, found the way Check found it (strictly, else SpacingTolerant). An
    // ellipsis quote's range runs from its first fragment to its last. Empty unless the quote is Verified.
    public IReadOnlyList<(int Start, int End)> Locate(string? quote)
    {
        if (Check(quote).Status != EvidenceStatus.Verified)
        {
            return [];
        }

        var fragments = SourceEvidence.Fragments(quote!);
        var (matches, _) = Find(fragments, tolerant: false);
        if (matches.Count == 0)
        {
            (matches, _) = Find(fragments, tolerant: true);
        }

        return matches.Select(m => (_tokens[m.Start].Start, _tokens[m.End - 1].End)).ToList();
    }

    // The character ranges of the passages that define a requirement ID (see IndexPassages); empty for
    // an ID the source only mentions, or does not hold.
    public IReadOnlyList<(int Start, int End)> DefinitionsOf(string id) =>
        _definitionsById.TryGetValue(id.Trim(), out var passages) ? passages : [];

    public EvidenceAssessment Assess(IEnumerable<(string? Quote, string? SourceId)>? evidence)
    {
        var checks = (evidence ?? []).Select(e => Check(e.Quote, e.SourceId)).ToList();
        var given = checks.Where(c => c.Status != EvidenceStatus.None).ToList();
        var status = given.Count == 0 ? EvidenceStatus.None
            : given.All(c => c.Status == EvidenceStatus.Verified) ? EvidenceStatus.Verified
            : EvidenceStatus.Unverified;
        return new EvidenceAssessment(status, checks);
    }

    // Token ranges [Start, End) where the fragments occur in order, each within MaxEllipsisGapChars
    // of the previous one and without omitting a material term (OmitsMaterialTerm) between them.
    // OmitsMaterial: some placement was rejected only because it omitted one. tolerant:
    // SpacingTolerant occurrences are accepted as well as strict ones.
    private (List<(int Start, int End)> Matches, bool OmitsMaterial) Find(List<List<SourceEvidence.Token>> fragments, bool tolerant)
    {
        var occurrences = fragments
            .Select(f => tolerant ? Strict(f).Concat(Tolerant(f)).Distinct().OrderBy(o => o.Start).ToList() : Strict(f))
            .ToList();
        if (occurrences.Any(o => o.Count == 0))
        {
            return ([], false);
        }

        var results = new List<(int Start, int End)>();
        var omitsMaterial = false;
        foreach (var first in occurrences[0])
        {
            var end = first.End;
            var complete = true;
            for (var k = 1; k < occurrences.Count && complete; k++)
            {
                var previousEnd = _tokens[end - 1].End;
                (int Start, int End)? next = null;
                foreach (var o in occurrences[k])
                {
                    if (o.Start < end || _tokens[o.Start].Start - previousEnd > SourceEvidence.MaxEllipsisGapChars)
                    {
                        continue;
                    }

                    if (OmitsMaterialTerm(end, o.Start))
                    {
                        omitsMaterial = true;
                        continue;
                    }

                    next = o;
                    break;
                }

                if (next is null)
                {
                    complete = false;
                }
                else
                {
                    end = next.Value.End;
                }
            }

            if (complete)
            {
                results.Add((first.Start, end));
            }
        }

        return (results, omitsMaterial);
    }

    // Whether the source tokens [from, to) an ellipsis skips contain a term whose omission can change
    // what the quote says: a negation ("shall not allow" quoted as "shall ... allow"), an exclusivity,
    // exception or condition ("only", "except", "unless", "if"), or a bound ("at least", "more than",
    // "<"). A deliberately conservative lexical list: it cannot recognise every change of meaning, and
    // any skip containing one of these terms is treated as altering the quote rather than judged.
    private bool OmitsMaterialTerm(int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            if (MaterialTerms.Contains(_tokens[i].Text))
            {
                return true;
            }
        }

        // Two-word terms with at least one word inside the skipped range.
        for (var i = Math.Max(from - 1, 0); i < to && i + 1 < _tokens.Count; i++)
        {
            if (MaterialPairs.Contains((_tokens[i].Text, _tokens[i + 1].Text)))
            {
                return true;
            }
        }

        return false;
    }

    // Tokens as Tokenize produces them: lower case, apostrophes dropped ("can't" -> "cant").
    private static readonly HashSet<string> MaterialTerms = new(StringComparer.Ordinal)
    {
        // negations
        "not", "no", "never", "none", "nor", "neither", "nobody", "nothing", "nowhere", "without",
        "cannot", "cant", "dont", "doesnt", "didnt", "wont", "shouldnt", "mustnt", "shant", "isnt",
        "arent", "wasnt", "werent", "couldnt", "wouldnt",
        // exclusivity, exceptions, conditions, optionality
        "only", "solely", "exclusively", "except", "excluding", "excepting", "unless", "if", "until",
        "provided", "optional", "optionally",
        // bounds and comparisons
        "than", "maximum", "minimum", "<", ">", "≤", "≥"
    };

    private static readonly HashSet<(string, string)> MaterialPairs =
    [
        ("at", "least"), ("at", "most"), ("up", "to")
    ];

    private List<(int Start, int End)> Strict(List<SourceEvidence.Token> fragment)
    {
        var results = new List<(int Start, int End)>();
        if (!_positions.TryGetValue(fragment[0].Text, out var starts))
        {
            return results;
        }

        foreach (var s in starts)
        {
            if (s + fragment.Count > _tokens.Count) continue;
            var match = true;
            for (var k = 1; k < fragment.Count && match; k++)
            {
                match = _tokens[s + k].Text == fragment[k].Text;
            }

            if (match) results.Add((s, s + fragment.Count));
        }

        return results;
    }

    // The quote's words, run together, found in the source's words run together - accepted only
    // where it starts and ends on source word boundaries, spans fewer source words than quote words
    // (the source merged some), and every merge is acceptable (AcceptsMerge).
    private List<(int Start, int End)> Tolerant(List<SourceEvidence.Token> fragment)
    {
        var results = new List<(int Start, int End)>();
        var quote = string.Concat(fragment.Select(t => t.Text));
        var quoteBoundaries = new List<int>(fragment.Count);
        var offset = 0;
        foreach (var token in fragment)
        {
            quoteBoundaries.Add(offset);
            offset += token.Text.Length;
        }

        for (var p = _joined.IndexOf(quote, StringComparison.Ordinal); p >= 0; p = _joined.IndexOf(quote, p + 1, StringComparison.Ordinal))
        {
            var a = Array.BinarySearch(_joinedStarts, p);
            var endPosition = p + quote.Length;
            var b = endPosition == _joined.Length ? _tokens.Count : Array.BinarySearch(_joinedStarts, endPosition);
            if (a < 0 || b < 0 || b - a >= fragment.Count)
            {
                continue;
            }

            if (AcceptsMerge(a, b, p, quote, quoteBoundaries))
            {
                results.Add((a, b));
            }
        }

        return results;
    }

    private bool AcceptsMerge(int a, int b, int p, string quote, List<int> quoteBoundaries)
    {
        for (var i = a; i < b; i++)
        {
            var start = _joinedStarts[i] - p;
            var length = _tokens[i].Text.Length;

            // Every source word boundary must also be a quote word boundary: the source may merge
            // the quote's words, never split them.
            if (quoteBoundaries.BinarySearch(start) < 0)
            {
                return false;
            }

            var inner = quoteBoundaries.Where(x => x > start && x < start + length).ToList();
            if (inner.Count == 0)
            {
                continue;
            }

            if (length < SourceEvidence.MinMergedTokenLength && inner.Count + 1 < SourceEvidence.MinMergedTokenWords)
            {
                return false; // short: more likely a different word ("island") than a merge
            }

            if (inner.Any(x => char.IsDigit(quote[x - 1]) && char.IsDigit(quote[x])))
            {
                return false; // never join two numbers ("1 5" is not "15")
            }
        }

        return true;
    }

    // The largest share of the quote's tokens found together in any window of the source as long as
    // the quote (the whole source when shorter). Classification only.
    private double BestOverlap(List<SourceEvidence.Token> quoteTokens)
    {
        if (quoteTokens.Count == 0 || _tokens.Count == 0)
        {
            return 0;
        }

        var wanted = quoteTokens.GroupBy(t => t.Text).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var window = Math.Min(quoteTokens.Count, _tokens.Count);
        var have = new Dictionary<string, int>(StringComparer.Ordinal);
        var overlap = 0;
        var best = 0;
        for (var i = 0; i < _tokens.Count; i++)
        {
            var added = _tokens[i].Text;
            if (wanted.TryGetValue(added, out var want))
            {
                var count = have.GetValueOrDefault(added);
                if (count < want) overlap++;
                have[added] = count + 1;
            }

            if (i >= window)
            {
                var removed = _tokens[i - window].Text;
                if (wanted.TryGetValue(removed, out var wantRemoved))
                {
                    var count = have[removed];
                    if (count <= wantRemoved) overlap--;
                    have[removed] = count - 1;
                }
            }

            best = Math.Max(best, overlap);
        }

        return best / (double)quoteTokens.Count;
    }

    // A requirement ID's passage: from a line that defines it (the ID first on the line, after an
    // optional bullet, list number or table separator) to the next line that is a section heading or
    // defines another ID. An ID that is only ever mentioned ("see FR-003") gets a passage from each
    // line mentioning it instead; mentions are ignored for an ID that is defined somewhere.
    private void IndexPassages(string text)
    {
        var ids = SrsIdExtractor.Extract(text);
        if (ids.Count == 0)
        {
            return;
        }

        var lines = new List<(int Start, string Text)>();
        var position = 0;
        foreach (var line in text.Split('\n'))
        {
            lines.Add((position, line));
            position += line.Length + 1;
        }

        var defines = new List<string?>(lines.Count);
        var mentions = new List<IReadOnlyList<string>>(lines.Count);
        foreach (var (_, line) in lines)
        {
            var inLine = SrsIdExtractor.Extract(line);
            mentions.Add(inLine);
            var rest = line[LinePrefix().Match(line).Length..];
            defines.Add(inLine.FirstOrDefault(id => rest.StartsWith(id, StringComparison.OrdinalIgnoreCase)));
        }

        int PassageEnd(int lineIndex)
        {
            for (var k = lineIndex + 1; k < lines.Count; k++)
            {
                if (defines[k] is not null || SourceChunker.IsSectionHeading(lines[k].Text))
                {
                    return lines[k].Start;
                }
            }

            return text.Length;
        }

        foreach (var id in ids)
        {
            var definingLines = Enumerable.Range(0, lines.Count)
                .Where(i => string.Equals(defines[i], id, StringComparison.OrdinalIgnoreCase)).ToList();
            var passageLines = definingLines.Count > 0
                ? definingLines
                : Enumerable.Range(0, lines.Count).Where(i => mentions[i].Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
            _passagesById[id] = passageLines.Select(i => (lines[i].Start, PassageEnd(i))).ToList();
            if (definingLines.Count > 0)
            {
                _definitionsById[id] = _passagesById[id];
            }
        }
    }

    [GeneratedRegex(@"^[\s\-*•·▪◦|\[(]*(?:\d+(?:\.\d+)*[.)]?\s+)?")]
    private static partial Regex LinePrefix();
}
