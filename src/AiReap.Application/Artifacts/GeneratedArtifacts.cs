using System.Collections;

namespace AiReap.Application.Artifacts;

// G2 — what one downstream AI generation call saved: the artifacts it created, in order, plus how many
// items of the AI response were not saved because the same artifact already exists (ArtifactFingerprints).
// It is the IReadOnlyList<ArtifactResponse> the generation services already return, so every caller and
// the JSON response (an array of the created artifacts) are unchanged; the API reports both counts in
// response headers (GenerationCountHeaders).
public sealed class GeneratedArtifacts : IReadOnlyList<ArtifactResponse>
{
    private readonly IReadOnlyList<ArtifactResponse> _created;

    public GeneratedArtifacts(IReadOnlyList<ArtifactResponse> created, int skippedDuplicates)
    {
        _created = created;
        SkippedDuplicates = skippedDuplicates;
    }

    public int SkippedDuplicates { get; }

    public int Count => _created.Count;

    public ArtifactResponse this[int index] => _created[index];

    public IEnumerator<ArtifactResponse> GetEnumerator() => _created.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
