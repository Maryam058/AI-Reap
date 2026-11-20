using System.Text;
using System.Text.RegularExpressions;

namespace AiReap.Application.Ai;

// Outcome of PromptBudgetReducer.Fit. Fits=false means even the parts that must never be dropped
// (system prompt, derived-artifact lists, a repair instruction, and at least some of the source)
// exceed the budget - the caller must refuse the request rather than send it.
public sealed record PromptReduction(
    string UserPrompt, bool Fits, bool WasReduced,
    int OriginalTokens, int FinalTokens, int KeptPassages, int TotalPassages)
{
    public int OmittedPassages => TotalPassages - KeptPassages;
}

// Fits one (system, user) prompt pair into a provider's per-request input-token budget, for
// providers whose free tier caps the size of a single request (Groq's tokens-per-minute limit).
// Deterministic and provider-independent - no extra AI calls, which a per-minute token cap could
// not afford anyway.
//
// A prompt that already fits is returned byte-for-byte unchanged. Otherwise, in order:
//   1. lossless clean-up: whitespace runs, a passage repeated directly after itself, repeated
//      boilerplate passages, and page headers/footers repeated throughout an extracted document -
//      never a repeated requirement statement, requirement ID or table cell (IsBoilerplate);
//   2. relevance selection over the source text: passages carrying requirement signals (modal
//      verbs, rules, numbers, list items, headings, and terms shared with the derived-artifact
//      context) are kept first; retrieved document excerpts come after the source text, in their
//      retrieval-rank order; passages with no signal go last. Kept passages stay in their original
//      order, every gap is marked, and a note at the top tells the model the input was condensed.
// Never dropped: the system prompt (it holds the output schema), context lists built by the
// pipeline ("Functional requirements derived so far:\n- FR-001: ...", clarification Q&A - the
// codes the model may reference), and GenerationSupport's repair instruction.
public static partial class PromptBudgetReducer
{
    // Deliberately pessimistic: English requirement prose measured ~4.8 characters per token on
    // gpt-oss (a 68.5k-character source was counted as 14,410 tokens), so 3.0 over-counts by ~60%,
    // leaving room for tokenizer variance, code/IDs/tables that tokenize densely, and Groq's own
    // pre-request estimate. Characters outside ASCII (accents, non-Latin scripts, symbols) are
    // counted as a full token each.
    public const double CharsPerToken = 3.0;

    // Per-request framing the provider adds around the messages (chat template, role headers,
    // gpt-oss system preamble and reasoning setting, JSON-mode instruction).
    public const int RequestOverheadTokens = 100;

    // Source blocks longer than this are split so selection can keep the relevant part of a long block.
    private const int MaxPassageChars = 1_200;
    private const int TargetPassageChars = 700;

    private const string OmissionMarker = "[... {0} passage(s) omitted to fit the AI provider's request budget ...]";

    // Tokens reserved for each "[... omitted ...]" marker when planning the selection: the marker as
    // rendered with a five-digit count (~25 tokens), plus the "\n\n" before it. Derived from the text
    // so the plan can never under-count it (a fixed 16 did, making long alternating selections
    // overshoot the budget and be refused although a fitting selection existed).
    private static readonly int MarkerTokens = EstimateTokens(string.Format(OmissionMarker, 99_999)) + 1;

    private const string CondensedNote =
        "[AI-REAP note: the input below exceeded the request budget of the AI provider serving this request and was " +
        "condensed automatically. {0} of {1} source passages were kept, chosen for requirement relevance and kept in " +
        "their original order; gaps are marked \"[... omitted ...]\". Base the answer only on what is shown. Do not " +
        "invent anything that may have been in an omitted passage - treat it as unknown and, where the response " +
        "format allows, flag it as missing or unclear.]\n\n";

    public static int EstimateTokens(string text)
    {
        var nonAscii = 0;
        foreach (var c in text)
        {
            if (c > '\u007f') nonAscii++;
        }

        return (int)Math.Ceiling((text.Length - nonAscii) / CharsPerToken) + nonAscii;
    }

    // Everything one chat request puts on the wire as input: both messages plus the framing.
    public static int EstimateRequestTokens(string systemPrompt, string userPrompt) =>
        EstimateTokens(systemPrompt) + EstimateTokens(userPrompt) + RequestOverheadTokens;

    public static PromptReduction Fit(string systemPrompt, string userPrompt, int maxInputTokens)
    {
        // The fixed per-request framing is charged with the system prompt: neither can be condensed.
        var systemTokens = EstimateTokens(systemPrompt) + RequestOverheadTokens;
        var originalTokens = systemTokens + EstimateTokens(userPrompt);
        if (originalTokens <= maxInputTokens)
        {
            return new PromptReduction(userPrompt, true, false, originalTokens, originalTokens, 0, 0);
        }

        var passages = Split(userPrompt);
        var sourceCount = passages.Count(p => p.Kind != PassageKind.Fixed);

        // 1. Lossless clean-up alone may be enough.
        var cleaned = Render(passages, keptSourcePassages: sourceCount, sourceCount, withNote: false);
        if (systemTokens + EstimateTokens(cleaned) <= maxInputTokens)
        {
            return new PromptReduction(cleaned, true, true, originalTokens, systemTokens + EstimateTokens(cleaned), sourceCount, sourceCount);
        }

        // 2. Relevance selection. Budget left after the fixed parts and the note. Gaps can outnumber
        // kept passages: one may follow the last rendered passage, and each fixed passage can split
        // a run of omitted ones, so a marker is reserved for each of those up front.
        var fixedCount = passages.Count(p => p.Kind == PassageKind.Fixed);
        var available = maxInputTokens - systemTokens
            - EstimateTokens(string.Format(CondensedNote, sourceCount, sourceCount))
            - passages.Where(p => p.Kind == PassageKind.Fixed).Sum(p => EstimateTokens(p.Text) + 1)
            - (fixedCount + 1) * MarkerTokens;

        Score(passages);
        foreach (var passage in passages.Where(p => p.Kind != PassageKind.Fixed).OrderBy(p => p.Tier).ThenByDescending(p => p.Density).ThenBy(p => p.Order))
        {
            // Worst case every kept passage opens a new gap that needs a marker.
            var cost = EstimateTokens(passage.Text) + 1 + MarkerTokens;
            if (cost <= available)
            {
                passage.Kept = true;
                available -= cost;
            }
        }

        var kept = passages.Count(p => p.Kind != PassageKind.Fixed && p.Kept);
        var keptSource = passages.Count(p => p.Kind == PassageKind.Source && p.Kept);
        var reduced = Render(passages, kept, sourceCount, withNote: true);
        var finalTokens = systemTokens + EstimateTokens(reduced);
        var fits = finalTokens <= maxInputTokens
            && (keptSource > 0 || passages.All(p => p.Kind != PassageKind.Source));

        return new PromptReduction(reduced, fits, true, originalTokens, finalTokens, kept, sourceCount);
    }

    private enum PassageKind
    {
        // Pipeline context lists and the repair instruction: always kept.
        Fixed,
        // The requirement source text.
        Source,
        // A retrieved project-document excerpt ("[notes.pdf#3]: ...").
        Excerpt
    }

    private sealed class Passage
    {
        public required string Text { get; init; }
        public required PassageKind Kind { get; init; }
        public required int Order { get; init; }
        // Separator placed before this passage when its predecessor is also kept.
        public required string Separator { get; init; }
        public int Tier { get; set; }
        public double Density { get; set; }
        public bool Kept { get; set; }
    }

    private static List<Passage> Split(string userPrompt)
    {
        var text = userPrompt.Replace("\r\n", "\n");
        text = TrailingSpace().Replace(text, "");
        text = InnerSpaceRun().Replace(text, " ");
        text = BlankLineRun().Replace(text, "\n\n").Trim('\n');

        var blocks = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var classified = blocks.Select(b => (Text: b, Kind: Classify(b))).ToList();

        // Page headers/footers left by document extraction: a line repeated three or more times,
        // found only where page furniture sits (a line of its own, or the first or last line of a
        // block), that IsBoilerplate. Only its first occurrence is kept. A repeated requirement
        // statement, ID or table cell is content, however often it repeats, and is never removed.
        var lineStats = new Dictionary<string, (int Count, bool OnlyAtEdges)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (blockText, _) in classified.Where(b => b.Kind == PassageKind.Source))
        {
            var lines = blockText.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var key = lines[i].Trim();
                if (key.Length is 0 or > 120) continue;
                var atEdge = i == 0 || i == lines.Length - 1;
                lineStats[key] = lineStats.TryGetValue(key, out var stats)
                    ? (stats.Count + 1, stats.OnlyAtEdges && atEdge)
                    : (1, atEdge);
            }
        }

        var lineCounts = lineStats
            .Where(s => s.Value.Count >= 3 && s.Value.OnlyAtEdges && IsBoilerplate(s.Key))
            .ToDictionary(s => s.Key, _ => false, StringComparer.OrdinalIgnoreCase);

        var seenBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousSourceBlock = null;
        var passages = new List<Passage>();
        foreach (var (blockText, kind) in classified)
        {
            var block = blockText;
            if (kind == PassageKind.Source)
            {
                block = string.Join('\n', block.Split('\n').Where(line =>
                {
                    var key = line.Trim();
                    if (!lineCounts.TryGetValue(key, out var seen)) return true;
                    lineCounts[key] = true;
                    return !seen;
                }));

                var blockKey = block.Trim();
                if (blockKey.Length == 0)
                {
                    continue; // nothing left after removing repeated page headers
                }

                // A repeated passage is dropped only when it adds nothing: a copy directly after
                // itself (an extraction or paste glitch), or boilerplate. The same requirement
                // stated again under another section is kept there too.
                var adjacentCopy = string.Equals(previousSourceBlock, blockKey, StringComparison.OrdinalIgnoreCase);
                previousSourceBlock = blockKey;
                if (!seenBlocks.Add(blockKey)
                    && ((adjacentCopy && WordCount(blockKey) >= MinBoilerplateWords && !LooksTabular(blockKey)) || IsBoilerplate(blockKey)))
                {
                    continue;
                }
            }

            var pieces = kind == PassageKind.Source && block.Length > MaxPassageChars
                ? SplitLongBlock(block)
                : [(block, "\n\n")];
            for (var i = 0; i < pieces.Count; i++)
            {
                passages.Add(new Passage
                {
                    Text = pieces[i].Text,
                    Kind = kind,
                    Order = passages.Count,
                    Separator = i == 0 ? "\n\n" : pieces[i].Separator
                });
            }
        }

        return passages;
    }

    // Fewer words than this and a repeated line or passage is treated as content (a table cell such
    // as "Yes", "High" or "Line manager"), never as a page header.
    private const int MinBoilerplateWords = 3;

    // Text that may be removed as a repeat: no requirement wording, no requirement ID, not a table
    // row, and long enough not to be a table cell. Anything else that repeats is kept.
    private static bool IsBoilerplate(string text) =>
        !RequirementSignal().IsMatch(text)
        && SrsIdExtractor.Extract(text).Count == 0
        && !LooksTabular(text)
        && WordCount(text) >= MinBoilerplateWords;

    private static bool LooksTabular(string text) => text.IndexOfAny(['|', '\t']) >= 0;

    private static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static PassageKind Classify(string block)
    {
        // GenerationSupport's repair instruction ("---\nYour previous response was rejected ...").
        if (block.StartsWith("---", StringComparison.Ordinal))
        {
            return PassageKind.Fixed;
        }

        var lines = block.Split('\n');

        // A pipeline context list: "Heading:" followed only by "- item" / "  continuation" lines
        // (GenerationSupport.BuildContextBlock, ClarificationContextBuilder).
        if (lines.Length >= 2 && lines[0].TrimEnd().EndsWith(':')
            && lines.Skip(1).All(l => l.StartsWith("- ", StringComparison.Ordinal) || l.StartsWith("  ", StringComparison.Ordinal)))
        {
            return PassageKind.Fixed;
        }

        return ExcerptLabel().IsMatch(block) ? PassageKind.Excerpt : PassageKind.Source;
    }

    // Split on line breaks first, then sentence ends, packing pieces up to TargetPassageChars.
    private static List<(string Text, string Separator)> SplitLongBlock(string block)
    {
        var units = new List<(string Text, string Separator)>();
        foreach (var line in block.Split('\n'))
        {
            var separator = "\n";
            if (line.Length <= MaxPassageChars)
            {
                units.Add((line, separator));
                continue;
            }

            foreach (var sentence in SentenceEnd().Split(line))
            {
                units.Add((sentence, separator));
                separator = " ";
            }
        }

        var pieces = new List<(string Text, string Separator)>();
        var current = new StringBuilder();
        var currentSeparator = "\n";
        foreach (var (text, separator) in units)
        {
            if (current.Length > 0 && current.Length + text.Length > TargetPassageChars)
            {
                pieces.Add((current.ToString(), currentSeparator));
                current.Clear();
            }

            if (current.Length == 0)
            {
                currentSeparator = separator;
            }
            else
            {
                current.Append(separator);
            }

            current.Append(text);
        }

        if (current.Length > 0)
        {
            pieces.Add((current.ToString(), currentSeparator));
        }

        return pieces;
    }

    private static void Score(List<Passage> passages)
    {
        // Terms the pipeline has already derived (FR titles, entity names, clarification topics):
        // source passages that talk about them matter most to the current stage.
        var contextTerms = passages.Where(p => p.Kind == PassageKind.Fixed)
            .SelectMany(p => Terms(p.Text))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var firstSource = passages.Where(p => p.Kind == PassageKind.Source).Select(p => p.Order).Take(2).ToHashSet();
        var excerptRank = 0;

        foreach (var passage in passages.Where(p => p.Kind != PassageKind.Fixed))
        {
            if (passage.Kind == PassageKind.Excerpt)
            {
                // Supporting material: after the source text with any signal, best-retrieved first.
                passage.Tier = 1;
                passage.Density = -excerptRank++;
                continue;
            }

            var text = passage.Text;
            double score = 0;
            score += 2 * Math.Min(RequirementSignal().Matches(text).Count, 8);
            score += Math.Min(NumberPattern().Matches(text).Count, 5);
            score += Math.Min(ListItem().Matches(text).Count, 5);
            score += Math.Min(Terms(text).Count(contextTerms.Contains), 10);

            // A short title-like line ("3.2 Leave approval") is cheap and keeps the structure readable.
            var trimmed = text.Trim();
            if (trimmed.Length <= 80 && !trimmed.Contains('\n') && !trimmed.EndsWith('.'))
            {
                score += 3;
            }

            // The opening passages usually state the scope and purpose.
            if (firstSource.Contains(passage.Order))
            {
                score += 3;
            }

            passage.Tier = score > 0 ? 0 : 2;
            passage.Density = score / Math.Max(1.0, text.Length / (double)TargetPassageChars);
        }
    }

    private static IEnumerable<string> Terms(string text) =>
        WordPattern().Matches(text).Select(m => m.Value.ToLowerInvariant()).Where(w => !StopWords.Contains(w)).Distinct();

    private static string Render(List<Passage> passages, int keptSourcePassages, int totalSourcePassages, bool withNote)
    {
        var sb = new StringBuilder();
        if (withNote)
        {
            sb.Append(string.Format(CondensedNote, keptSourcePassages, totalSourcePassages));
        }

        var atStart = true;
        var previousKept = false;
        var omitted = 0;

        void AppendMarker()
        {
            if (!atStart) sb.Append("\n\n");
            sb.Append(string.Format(OmissionMarker, omitted));
            atStart = false;
            omitted = 0;
        }

        foreach (var passage in passages)
        {
            if (withNote && passage.Kind != PassageKind.Fixed && !passage.Kept)
            {
                omitted++;
                previousKept = false;
                continue;
            }

            if (omitted > 0)
            {
                AppendMarker();
            }

            if (!atStart)
            {
                sb.Append(previousKept ? passage.Separator : "\n\n");
            }

            sb.Append(passage.Text);
            atStart = false;
            previousKept = true;
        }

        if (omitted > 0)
        {
            AppendMarker();
        }

        return sb.ToString();
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "that", "this", "with", "from", "have", "will", "shall", "must", "should", "their", "there", "which",
        "when", "where", "what", "been", "into", "each", "they", "them", "then", "than", "also", "only",
        "such", "some", "more", "most", "other", "about", "after", "before", "being", "does", "were", "your",
        "yet", "answered", "treat", "still", "unresolved", "propose", "assumption", "needed", "flag",
        "derived", "proposed", "requirements", "functional", "clarifications", "gathered"
    };

    [GeneratedRegex(@"[ \t]+$", RegexOptions.Multiline)]
    private static partial Regex TrailingSpace();

    [GeneratedRegex(@"(?<=\S)[ \t]{2,}")]
    private static partial Regex InnerSpaceRun();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLineRun();

    [GeneratedRegex(@"^(?:[^\n]*:\n)?\[[^\]\n]+#\d+\]: ")]
    private static partial Regex ExcerptLabel();

    [GeneratedRegex(@"(?<=[.!?;])\s+")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\b(?:shall|must|should|required?|requires|mandatory|may not|cannot|can't|not allowed|only|allow(?:s|ed)?|prevent(?:s|ed)?|ensure(?:s|d)?|at least|at most|maximum|minimum|within|approv(?:e|es|ed|al)|reject(?:s|ed)?|notif(?:y|ies|ication)|permission|role|rule|validat(?:e|es|ion)|audit|report)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RequirementSignal();

    [GeneratedRegex(@"\b\d+(?:[.,]\d+)?%?")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^\s*(?:\d+[.)]|[-*•])\s", RegexOptions.Multiline)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\b[A-Za-z][A-Za-z-]{3,}\b")]
    private static partial Regex WordPattern();
}
