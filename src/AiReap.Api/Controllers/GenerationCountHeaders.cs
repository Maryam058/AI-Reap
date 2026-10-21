using AiReap.Application.Artifacts;

namespace AiReap.Api.Controllers;

// G2 — how many artifacts a downstream generation call created and how many items of the AI response it
// did not save because the same artifact already exists. Reported in response headers so the JSON body
// (the created artifacts) keeps its shape for every existing client.
public static class GenerationCountHeaders
{
    public const string Created = "X-AiReap-Artifacts-Created";
    public const string Skipped = "X-AiReap-Artifacts-Skipped";

    public static void Write(HttpResponse response, int created, int skipped)
    {
        response.Headers[Created] = created.ToString(System.Globalization.CultureInfo.InvariantCulture);
        response.Headers[Skipped] = skipped.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // For a generation result list: its counts when it is a GeneratedArtifacts, nothing otherwise.
    public static void Write(HttpResponse response, IReadOnlyList<ArtifactResponse>? result)
    {
        if (result is GeneratedArtifacts generated)
        {
            Write(response, generated.Count, generated.SkippedDuplicates);
        }
    }
}
