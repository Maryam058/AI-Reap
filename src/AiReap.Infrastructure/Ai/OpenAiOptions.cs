namespace AiReap.Infrastructure.Ai;

public class OpenAiOptions
{
    public const string SectionName = "Ai:OpenAI";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "text-embedding-3-small";
}
