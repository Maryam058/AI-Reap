using AiReap.Application.Artifacts.Payloads;

namespace AiReap.Application.Ai.Pipeline;

// Where a clarification question's evidence quotes are written in its requirement source (no AI call,
// no I/O). Used when a question is made (ClarificationCandidateValidator) and when a question saved
// without located evidence is read (ClarificationSourceLinks), so both link the same passage.
//   - A quote is located only where SourceEvidenceIndex verifies it (word for word, spacing-tolerant
//     for PDF text), and its excerpt is the source's own text there - never the model's wording.
//   - Wording written in several places is never resolved to an arbitrary occurrence. The occurrence
//     chosen is the only one inside a section the question cites (hints: its reason's links, the
//     sections the model claimed), else the only one in the same section as the question's other
//     evidence that has one place (the nearest to it, when that is strictly nearest). With nothing to
//     tell them apart the quote is left unplaced (counted in Located.Ambiguous): a question is never
//     linked to an occurrence that may not be the one that raised it. Occurrences says how many there are.
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
    // is not verified, or whose occurrence cannot be told apart, is left out; one that is the whole source
    // (a one-line source) is its whole text.
    public List<SourceReferencePayload> Locate(IEnumerable<string?> quotes, IReadOnlyList<SourceReferencePayload>? hints = null) =>
        LocateAll(quotes, hints).Places;

    // Locate, also counting the verified quotes left out because they are written in several places
    // and nothing tells which one raised the question. Anchors: passages already placed for the question
    // (its other stored evidence), used as its single-place evidence is.
    public Located LocateAll(
        IEnumerable<string?> quotes, IReadOnlyList<SourceReferencePayload>? hints = null, IReadOnlyList<SourceReferencePayload>? anchors = null)
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

        return Choose(candidates, hints ?? [], anchors ?? []);
    }

    public sealed record Located(List<SourceReferencePayload> Places, int Ambiguous);

    // One place per quote (see the class comment), in quote order.
    public Located Choose(
        List<List<SourceReferencePayload>> candidates, IReadOnlyList<SourceReferencePayload> hints, IReadOnlyList<SourceReferencePayload> anchors)
    {
        var single = candidates.Where(c => c.Count == 1).Select(c => c[0]).Concat(anchors.Where(a => a.Line >= 0)).ToList();
        var chosen = new List<SourceReferencePayload>();
        var ambiguous = 0;
        foreach (var places in candidates)
        {
            var place = places.Count == 1 ? places[0] : Pick(places, hints, single);
            if (place is null)
            {
                ambiguous++;
                continue;
            }

            if (places.Count > 1)
            {
                place.Occurrences = places.Count;
            }

            if (!chosen.Any(c => c.Line == place.Line && c.EndLine == place.EndLine && c.Excerpt == place.Excerpt))
            {
                chosen.Add(place);
            }
        }

        return new Located(chosen, ambiguous);
    }

    // The occurrence the question's context singles out, or null when nothing does (see the class comment).
    private SourceReferencePayload? Pick(
        List<SourceReferencePayload> places, IReadOnlyList<SourceReferencePayload> hints, List<SourceReferencePayload> anchors)
    {
        var cited = places.Where(p => hints.Any(h => h.Line >= 0 && h.Line <= p.Line && p.EndLine <= Math.Max(h.EndLine, h.Line + 1))).ToList();
        if (cited.Count == 1)
        {
            return cited[0];
        }

        var pool = cited.Count > 1 ? cited : places;
        var sections = anchors.Select(a => _references.SectionOf(a.Line)).OfType<SourceSection>().ToHashSet();
        var sameSection = pool.Where(p => _references.SectionOf(p.Line) is { } s && sections.Contains(s)).ToList();
        if (sameSection.Count <= 1)
        {
            return sameSection.FirstOrDefault();
        }

        var distances = sameSection.Select(p => (Place: p, Distance: anchors.Min(a => Math.Abs(a.Line - p.Line)))).OrderBy(d => d.Distance).ToList();
        return distances[0].Distance < distances[1].Distance ? distances[0].Place : null;
    }
}
