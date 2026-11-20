using System.Text.RegularExpressions;

namespace AiReap.Application.Ai.Pipeline;

// Whether a passage cited for a clarification question is about what the question asks (no AI call, no
// I/O). A reviewer must be able to see from the passage alone why the question was asked, so sharing a
// word the whole document uses ("Careway", "referral" in a Careway integration SRS) is not enough:
//   - Common terms: in a long document (at least MinSentences sentences), the terms used in more than
//     CommonShare of its sentences. They name the document's general subject, not any one rule. A short
//     document has none, so every shared term counts there, as before.
//   - A passage supports a question when they share at least one term that is not common. When every
//     term of the question is common, any shared term will do (the question can say nothing more
//     specific). Numbers and requirement IDs never count.
// Used when a question is made (ClarificationCandidateValidator: a passage that does not support its
// question is not attached; none supporting rejects the question as irrelevant-evidence), when stored
// evidence is read (ClarificationSourceReferences: an unsupported passage is not shown beside a
// supporting one), and when a passage is checked as resolving two rules (RuleRelations).
// Limits: lexical. A passage that shares one specific word without being about the question (an email
// to a provider's PA for a question about delivering documents by email) still counts; the model's
// quotes and the reviewer remain the judge of meaning.
public static partial class EvidenceSupport
{
    public const int MinSentences = 40;
    public const double CommonShare = 0.10;

    // The common terms (see the class comment) of a document read as the terms of each of its sentences.
    public static HashSet<string> CommonTerms(IReadOnlyList<HashSet<string>> sentenceTerms)
    {
        var common = new HashSet<string>(StringComparer.Ordinal);
        if (sentenceTerms.Count < MinSentences)
        {
            return common;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var terms in sentenceTerms)
        {
            foreach (var term in terms)
            {
                counts[term] = counts.GetValueOrDefault(term) + 1;
            }
        }

        foreach (var (term, count) in counts)
        {
            if (count > CommonShare * sentenceTerms.Count)
            {
                common.Add(term);
            }
        }

        return common;
    }

    // The common terms of a source text: each non-blank line split into sentences.
    public static HashSet<string> CommonTerms(string? sourceText) => CommonTerms(
        SourceReferenceIndex.Lines(sourceText ?? string.Empty)
            .Where(l => l.Trim().Length > 0)
            .SelectMany(l => SentenceBreak().Split(l.Trim()))
            .Where(s => s.Length > 0)
            .Select(s => ClarificationCandidateValidator.Terms(s))
            .Where(t => t.Count > 0)
            .ToList());

    public static PassageSupport Assess(IReadOnlySet<string> questionTerms, IReadOnlySet<string> passageTerms, IReadOnlySet<string> common)
    {
        var words = questionTerms.Where(t => !t.Any(char.IsDigit)).ToList();
        var shared = words.Where(passageTerms.Contains).ToList();
        var specific = shared.Where(t => !common.Contains(t)).ToList();
        var supports = specific.Count > 0 || (shared.Count > 0 && words.All(common.Contains));
        return new PassageSupport(supports, shared, specific);
    }

    [GeneratedRegex(@"(?<=[.;!?])\s+")]
    private static partial Regex SentenceBreak();
}

// Shared: the question's terms the passage holds; Specific: those that are not common in the document.
public sealed record PassageSupport(bool Supports, IReadOnlyList<string> Shared, IReadOnlyList<string> Specific);
