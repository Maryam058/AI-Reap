namespace AiReap.Application.Artifacts.Payloads;

// §12 — ArtifactType.UserStory.
public class UserStoryPayload
{
    public string Persona { get; set; } = string.Empty;
    public string ValueStatement { get; set; } = string.Empty;
}
