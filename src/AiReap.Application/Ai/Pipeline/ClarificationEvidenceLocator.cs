using AiReap.Application.Artifacts.Payloads;

namespace AiReap.Application.Ai.Pipeline;

// Where a clarification question's evidence quotes are written in its requirement source (no AI call,
// no I/O). Used when a question is made (ClarificationCandidateValidator) and when a question saved
// without located evidence is read (ClarificationSourceLinks), so both link the same passage.
//   - A quote is located only where SourceEvidenceIndex verifies it (word for word, spacing-tolerant
//     for PDF text), and its excerpt is the source's own text there - never the model's wording.
//   - Wording written in several places is not resolved to the first by default. In order, the
//     occurrence chosen is the one inside a section the question cites (hints: its reason's links, the
//     sections the model claimed), else the one in the same section as - or nearest to - the
//     question's other evidence that has only one place. With nothing to tell them apart it is the
//     first, and Occurrences says how many there are so the viewer can say so.
public sealed class ClarificationEvidenceLocator
{
    private readonly SourceReferenceIndex _references;
    private readonly Lazy<SourceEvidenceIndex> _evidence;
    private readonly string _sourceText;
    private readonly Lazy<string> _normalized;
    private readonly int _sourceLength;

    public ClarificationEvidenceLocator(SourceReferenceIndex references, SourceEvidenceIndex evidence, string? sourceText)
        : this(references, new Lazy<SourceEvidenceIndex>(evidence), sourceText)
    {
    }

    private ClarificationEvidenceLocator(SourceReferenceIndex references, Lazy<SourceEvidenceIndex> evidence, string? sourceText)
    {
        _references = references;
        _evidence = evidence;
        _sourceText = sourceText ?? string.Empty;
        _normalized = new Lazy<string>(() => SourceEvidence.Normalize(_sourceText));
        _sourceLength = _sourceText.Length;
    }

    // For reading saved questions: the word index of the source is built only if a quote has to be
    // placed, so a read where every stored passage still holds costs no more than resolving its links.
    public static ClarificationEvidenceLocator ForSource(Guid sourceId, string? sourceText) =>
        new(SourceReferenceIndex.Build(sourceId, sourceText), new Lazy<SourceEvidenceIndex>(() => SourceEvidenceIndex.Build(sourceText)), sourceText);

    public SourceReferenceIndex References => _references;
    public SourceEvidenceIndex Evidence => _evidence.Value;

    // The quotes' passages in quote order (the first is the primary one), duplicates dropped. A quote that
    // is not verified is left out; one that is the whole source (a one-line source) is its whole text.
    public List<SourceReferencePayload> Locate(IEnumerable<string?> quotes, IReadOnlyList<SourceReferencePayload>? hints = null)
    {
        var candidates = new List<List<SourceReferencePayload>>();
        foreach (var raw in quotes)
        {
            var quote = raw?.Trim().Trim('"', '“', '”');
            if (string.IsNullOrWhiteSpace(quote))
            {
                continue;
            }

            // Verified quotes are placed as written (punctuation included) where that is found, else
            // where the word-for-word check found them (other spacing, hyphenation, table separators).
            IReadOnlyList<(int Start, int End)> ranges;
            if (_sourceLength > 0 && _normalized.Value.Length > 0 && SourceEvidence.Normalize(quote) == _normalized.Value)
            {
                ranges = [(0, _sourceLength)];
            }
            else if (Evidence.Check(quote).Status == EvidenceStatus.Verified && _references.PhraseOccurrences(quote) is { Count: > 0 } exact)
            {
                ranges = exact;
            }
            else
            {
                // The word-for-word match ends on a word; the quote's own closing punctuation is kept.
                var closing = char.IsPunctuation(quote[^1]) ? quote[^1] : (char?)null;
                ranges = Evidence.Locate(quote)
                    .Select(r => closing is { } c && r.End < _sourceLength && _sourceText[r.End] == c ? (r.Start, r.End + 1) : r)
                    .ToList();
            }
            var places = ranges.Select(r => _references.LocateRange(r.Start, r.End)).OfType<SourceReferencePayload>().ToList();
            if (places.Count > 0)
            {
                candidates.Add(places);
            }
        }

        return Choose(candidates, hints ?? []);
    }

    // One place per quote (see the class comment), in quote order.
    public List<SourceReferencePayload> Choose(List<List<SourceReferencePayload>> candidates, IReadOnlyList<SourceReferencePayload> hints)
    {
        var anchors = candidates.Where(c => c.Count == 1).Select(c => c[0]).ToList();
        var chosen = new List<SourceReferencePayload>();
        foreach (var places in candidates)
        {
            var place = places.Count == 1 ? places[0] : Pick(places, hints, anchors);
            if (places.Count > 1)
            {
                place.Occurrences = places.Count;
            }

            if (!chosen.Any(c => c.Line == place.Line && c.EndLine == place.EndLine && c.Excerpt == place.Excerpt))
            {
                chosen.Add(place);
            }
        }

        return chosen;
    }

    private SourceReferencePayload Pick(
        List<SourceReferencePayload> places, IReadOnlyList<SourceReferencePayload> hints, List<SourceReferencePayload> anchors)
    {
        var cited = places.Where(p => hints.Any(h => h.Line >= 0 && h.Line <= p.Line && p.EndLine <= Math.Max(h.EndLine, h.Line + 1))).ToList();
        if (cited.Count > 0)
        {
            return cited[0];
        }

        if (anchors.Count > 0)
        {
            var sections = anchors.Select(a => _references.SectionOf(a.Line)).OfType<SourceSection>().ToHashSet();
            var sameSection = places.Where(p => _references.SectionOf(p.Line) is { } s && sections.Contains(s)).ToList();
            return (sameSection.Count > 0 ? sameSection : places)
                .MinBy(p => anchors.Min(a => Math.Abs(a.Line - p.Line)))!;
        }

        return places[0];
    }
}
