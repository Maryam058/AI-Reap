using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts.Payloads;

namespace AiReap.Application.Artifacts;

// A clarification question's links into its requirement source - SourceReferences (the places its Reason
// names) and SourceEvidence (the passages that raised it) - edited as a JSON object so no other field is
// touched. Stored keys may be PascalCase (generation) or camelCase (human edits).
//
// Reads (WithSourceLinks): every question is returned linked as far as its source verifies, and nothing
// is saved - the question, its answer, status and review are never changed, so a read is idempotent:
//   - stored links and evidence are kept where they still hold in the source (SourceReferenceIndex.Holds);
//     evidence that does not is placed again from its own quoted words, and a reason link that does not
//     is dropped and the reason resolved again. Evidence that cannot be placed stays, marked Line -1.
//     Evidence whose words are written in several places is kept only where the question's links and
//     other evidence still single that occurrence out (ClarificationEvidenceLocator) - a question saved
//     when the first occurrence was taken by default is not linked to a guess;
//   - a question without located evidence (saved before RequirementAnalysis-v3, or whose evidence was
//     never placed) gets it recovered, in this order, each step only from what the source verifies:
//       1. RecoveryGeneration: the quotes the model gave for it in the analysis response that raised it
//          (its AIExecution audit row), matched to the question by its saved or first wording;
//       2. RecoveryQuoted: words the question or its reason quote ("the term 'promptly'") - four words or
//          more as evidence, a shorter term only where the source writes it once (or once inside the
//          sections the reason cites), as the sentence holding it.
//     A requirement ID, section number or topic word the question merely names is never evidence: a
//     question none of these place is "unresolved".
//   - SourceLinkStatus says which case the question is in (see ClarificationQuestionPayload), and
//     IsGrounded whether it may be shown at all (ClarificationVisibility): only a question with verified
//     evidence it is about - both statements, for a contradiction - is ever shown.
// Restricted callers (S2) never get any of this - RequirementEvidenceFields.RedactForRestrictedCaller
// removes it with the other source quotes.
// Edits: server-owned - whatever the client sends, the previous version's links are kept.
public static partial class ClarificationSourceReferences
{
    public const string Key = nameof(ClarificationQuestionPayload.SourceReferences);
    private const string EvidenceKey = nameof(ClarificationQuestionPayload.SourceEvidence);
    private const string RefsKey = nameof(ClarificationQuestionPayload.RequirementRefs);
    private const string StatusKey = nameof(ClarificationQuestionPayload.SourceLinkStatus);

    public const string StatusPassage = "passage";
    public const string StatusSection = "section";
    public const string StatusUnresolved = "unresolved";

    public const string RecoveryGeneration = "generation-response";
    public const string RecoveryQuoted = "quoted-in-question";

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    // Fields the server derives from the source; a human edit never replaces them.
    public static readonly IReadOnlyList<string> ServerOwnedKeys =
    [
        Key, EvidenceKey, RefsKey, nameof(ClarificationQuestionPayload.RelatedDocuments), StatusKey
    ];

    // Fields that quote the requirement source or project documents (withheld from restricted callers).
    public static readonly IReadOnlyList<string> SourceTextKeys =
    [
        Key, EvidenceKey, nameof(ClarificationQuestionPayload.RelatedDocuments)
    ];

    // What is known of how a question was made, for recovering its evidence: the items of the analysis
    // response(s) that raised it, and its wording when first saved (before any human edit).
    public sealed record GenerationTrace(IReadOnlyList<GeneratedClarification> Items, string? OriginalQuestion);

    // Whether the question has no evidence placed in the source, so a read tries to recover some
    // (and the generation trace is worth loading).
    public static bool NeedsEvidence(string dataJson) =>
        Read(dataJson) is not { } data || !(data.SourceEvidence ?? []).Any(e => e.Line >= 0);

    // Whether a question linked by WithSourceLinks has the evidence a shown question needs: a verified
    // passage the question is about (ClarificationCandidateValidator.Relevant), and for a contradiction
    // both statements at different places. A question without it is never shown (ClarificationVisibility).
    public static bool IsGrounded(string linkedJson)
    {
        if (Read(linkedJson) is not { } data || data.SourceLinkStatus != StatusPassage)
        {
            return false;
        }

        var located = (data.SourceEvidence ?? []).Where(e => e.Line >= 0).ToList();
        if (located.Count == 0 || !ClarificationCandidateValidator.Relevant(data.Question, located))
        {
            return false;
        }

        return ClarificationCandidateValidator.NormalizeKind(data.Kind) != "contradiction"
            || located.Select(e => (e.Line, e.EndLine)).Where((p, i) => !located.Take(i).Any(o => p.Line < o.EndLine && o.Line < p.EndLine)).Count() >= 2;
    }

    // The question's data linked to its source as far as the source verifies (see the class comment).
    public static string WithSourceLinks(string dataJson, Guid sourceId, ClarificationEvidenceLocator locator, GenerationTrace? trace = null)
    {
        if (Parse(dataJson) is not { } payload || Read(dataJson) is not { } data)
        {
            return dataJson;
        }

        var index = locator.References;
        SourceReferencePayload Own(SourceReferencePayload r)
        {
            if (r.SourceId == Guid.Empty) r.SourceId = sourceId;
            return r;
        }

        // The reason's links: stored ones that hold, the reason resolved again for the rest.
        List<SourceReferencePayload> links;
        if (Lookup(payload, Key) is JsonArray)
        {
            var stored = (data.SourceReferences ?? []).Select(Own).ToList();
            links = stored.Where(index.Holds).ToList();
            if (links.Count < stored.Count)
            {
                links.AddRange(index.Resolve(data.Reason).Where(r => !links.Any(l => r.Start < l.Start + l.Label.Length && l.Start < r.Start + r.Label.Length)));
            }

            links = links.OrderBy(r => r.Start).ToList();
        }
        else
        {
            links = index.Resolve(data.Reason);
        }

        // The evidence: stored passages that hold, moved back onto their own words where they do not.
        var evidence = new List<SourceReferencePayload>();
        foreach (var stored in (data.SourceEvidence ?? []).Select(Own))
        {
            if (index.Holds(stored))
            {
                evidence.Add(stored);
                continue;
            }

            if (locator.Locate([stored.Excerpt ?? stored.Label], links).FirstOrDefault() is { } moved)
            {
                moved.Recovery = stored.Recovery;
                evidence.Add(moved);
                continue;
            }

            stored.Line = -1;
            stored.EndLine = -1;
            evidence.Add(stored);
        }

        // Wording written in several places stays linked only where the question still singles it out.
        var single = evidence.Where(e => e.Line >= 0 && e.Occurrences is null or <= 1).ToList();
        foreach (var repeated in evidence.Where(e => e.Line >= 0 && e.Occurrences > 1))
        {
            var confirmed = locator.LocateAll([repeated.Excerpt], links, single).Places;
            if (!confirmed.Any(c => c.Line == repeated.Line && c.EndLine == repeated.EndLine))
            {
                repeated.Line = -1;
                repeated.EndLine = -1;
            }
        }

        if (!evidence.Any(e => e.Line >= 0) && Recover(data, links, locator, trace) is { Count: > 0 } recovered)
        {
            evidence = recovered.Concat(evidence).ToList();
        }

        Set(payload, Key, links);
        if (evidence.Count > 0 || Lookup(payload, EvidenceKey) is not null)
        {
            Set(payload, EvidenceKey, evidence);
        }

        if (Lookup(payload, RefsKey) is not JsonArray && evidence.Any(e => e.Line >= 0))
        {
            Set(payload, RefsKey, ClarificationCandidateValidator.RequirementRefsOf(evidence.Where(e => e.Line >= 0).ToList()));
        }

        Set(payload, StatusKey, evidence.Any(e => e.Line >= 0) ? StatusPassage : links.Count > 0 ? StatusSection : StatusUnresolved);
        return payload.ToJsonString();
    }

    public static JsonObject KeepOnEdit(string previousJson, JsonObject edited)
    {
        var previous = Parse(previousJson);
        foreach (var key in ServerOwnedKeys)
        {
            Remove(edited, key);
            if (previous is not null && Lookup(previous, key) is { } kept)
            {
                edited[key] = kept.DeepClone();
            }
        }

        return edited;
    }

    // ---- recovery ------------------------------------------------------------------------------

    private static List<SourceReferencePayload> Recover(
        ClarificationQuestionPayload data, List<SourceReferencePayload> links, ClarificationEvidenceLocator locator, GenerationTrace? trace)
    {
        var fromGeneration = FromGeneration(data, links, locator, trace);
        if (fromGeneration.Count > 0)
        {
            return Mark(fromGeneration, RecoveryGeneration);
        }

        return Mark(FromQuotedWords(data, links, locator), RecoveryQuoted);
    }

    private static List<SourceReferencePayload> Mark(List<SourceReferencePayload> evidence, string recovery)
    {
        evidence.ForEach(e => e.Recovery = recovery);
        return evidence;
    }

    // The quotes of the response item that raised this question - the same question (as saved now, or as
    // first saved), else the same reason - verified and placed now.
    private static List<SourceReferencePayload> FromGeneration(
        ClarificationQuestionPayload data, List<SourceReferencePayload> links, ClarificationEvidenceLocator locator, GenerationTrace? trace)
    {
        if (trace is null || trace.Items.Count == 0)
        {
            return [];
        }

        var keys = new[] { data.Question, trace.OriginalQuestion }
            .Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => AiResultMerge.Key(q!)).Where(k => k.Length > 0).ToHashSet();
        var items = trace.Items.Where(i => keys.Contains(AiResultMerge.Key(i.Question))).ToList();
        if (items.Count == 0 && !string.IsNullOrWhiteSpace(data.Reason))
        {
            var reason = AiResultMerge.Key(data.Reason);
            items = trace.Items.Where(i => i.Reason is { } r && AiResultMerge.Key(r) == reason).ToList();
        }

        var found = new List<SourceReferencePayload>();
        foreach (var item in items)
        {
            var hints = links.Concat(locator.References.Resolve(item.Reason, item.Claims)).ToList();
            foreach (var place in locator.Locate(item.Quotes, hints))
            {
                if (!found.Any(f => f.Line == place.Line && f.EndLine == place.EndLine && f.Excerpt == place.Excerpt))
                {
                    found.Add(place);
                }
            }
        }

        return found;
    }

    // Words the question or reason put in quotes, where the source writes them (see the class comment).
    private static List<SourceReferencePayload> FromQuotedWords(
        ClarificationQuestionPayload data, List<SourceReferencePayload> links, ClarificationEvidenceLocator locator)
    {
        var index = locator.References;
        var found = new List<SourceReferencePayload>();
        foreach (var text in new[] { data.Question, data.Reason })
        {
            foreach (Match match in QuotedWords().Matches(text ?? string.Empty))
            {
                var phrase = match.Groups["t"].Value.Trim();
                // A quoted heading title is already a section link of the reason.
                if (phrase.Length < 4 || links.Any(l => !l.ExcerptIsQuote && string.Equals(l.Label, phrase, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                SourceReferencePayload? place;
                if (SourceEvidence.Normalize(phrase).Split(' ').Length >= SourceEvidence.MinQuoteWords)
                {
                    place = locator.Locate([phrase], links).FirstOrDefault();
                }
                else
                {
                    var occurrences = index.PhraseOccurrences(phrase)
                        .Select(o => (Range: o, Place: index.LocateRange(o.Start, o.End)))
                        .Where(o => o.Place is not null && !IsHeadingLine(index, o.Place.Line))
                        .ToList();
                    var cited = occurrences.Where(o => links.Any(l => l.Line <= o.Place!.Line && o.Place.Line < l.EndLine)).ToList();
                    var only = occurrences.Count == 1 ? occurrences : cited.Count == 1 ? cited : [];
                    place = only.Count == 1 && index.SentenceAround(only[0].Range.Start, only[0].Range.End) is var sentence
                        ? index.LocateRange(sentence.Start, sentence.End)
                        : null;
                }

                if (place is not null && !found.Any(f => f.Line == place.Line && f.Excerpt == place.Excerpt))
                {
                    found.Add(place);
                }
            }
        }

        return found;
    }

    private static bool IsHeadingLine(SourceReferenceIndex index, int line) =>
        index.Sections.Any(s => s.Line == line);

    // ---- JSON ----------------------------------------------------------------------------------

    private static ClarificationQuestionPayload? Read(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ClarificationQuestionPayload>(json, ReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Replaced where the key already is (its stored casing and position kept), so a repeated read gives
    // the same JSON; added at the end otherwise.
    private static void Set<T>(JsonObject payload, string key, T value)
    {
        var existing = payload.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
        payload[existing] = JsonSerializer.SerializeToNode(value);
    }

    private static void Remove(JsonObject payload, string key)
    {
        foreach (var k in payload.Select(kv => kv.Key).Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            payload.Remove(k);
        }
    }

    private static JsonNode? Lookup(JsonObject payload, string key) =>
        payload.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    // "the term "promptly"", 'promptly', curly quotes. Straight single quotes only around a phrase (after
    // a space or bracket, before a space or punctuation), so an apostrophe never opens one.
    [GeneratedRegex(@"[""“](?<t>[^""“”\n]{3,300})[""”]|‘(?<t>[^‘’\n]{3,300})’|(?<=^|[\s(])'(?<t>[^'\n]{3,300})'(?=[\s.,;:)!?]|$)")]
    private static partial Regex QuotedWords();
}
