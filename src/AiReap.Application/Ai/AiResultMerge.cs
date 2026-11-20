using System.Text;

namespace AiReap.Application.Ai;

// Deterministic merging of list results from several AI calls over parts of one source
// (GenerationSupport.CallAiAndParseChunkedAsync). Neighbouring parts often report the same actor,
// capability or open question in slightly different words; entries are compared by a normalized
// key (case, punctuation, leading articles and plural "s" ignored) and the first wording seen wins,
// so the merged order follows the source order. No AI call is involved.
public static class AiResultMerge
{
    public static List<string> DistinctTexts(IEnumerable<string> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var item in items)
        {
            var key = Key(item);
            if (key.Length > 0 && seen.Add(key))
            {
                result.Add(item.Trim());
            }
        }

        return result;
    }

    // Open questions: the same question (normalized), or the same topic asked with mostly the same
    // words, is one question. The first is kept; a missing reason is filled from a duplicate.
    public static List<T> DistinctQuestions<T>(
        IEnumerable<T> items, Func<T, string> topic, Func<T, string> question,
        Func<T, string>? reason = null, Action<T, string>? setReason = null)
    {
        var kept = new List<(T Item, string TopicKey, string QuestionKey, HashSet<string> Words)>();
        foreach (var item in items)
        {
            var topicKey = Key(topic(item));
            var questionKey = Key(question(item));
            var words = questionKey.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

            var duplicateOf = kept.FindIndex(k =>
                (questionKey.Length > 0 && k.QuestionKey == questionKey)
                || (topicKey.Length > 0 && k.TopicKey == topicKey && Jaccard(k.Words, words) >= 0.6));
            if (duplicateOf < 0)
            {
                kept.Add((item, topicKey, questionKey, words));
                continue;
            }

            var first = kept[duplicateOf].Item;
            if (reason is not null && setReason is not null && string.IsNullOrWhiteSpace(reason(first)) && !string.IsNullOrWhiteSpace(reason(item)))
            {
                setReason(first, reason(item));
            }
        }

        return kept.Select(k => k.Item).ToList();
    }

    // "The Line Managers." -> "line manager"
    public static string Key(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0] is "the" or "a" or "an")
        {
            words.RemoveAt(0);
        }

        return string.Join(' ', words.Select(Singular));
    }

    private static string Singular(string word) =>
        word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) ? word[..^1] : word;

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return 1;
        }

        var intersection = a.Count(b.Contains);
        return intersection / (double)(a.Count + b.Count - intersection);
    }
}
