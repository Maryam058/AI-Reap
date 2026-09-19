namespace AiReap.Infrastructure.Ai;

public class AnthropicOptions
{
    public const string SectionName = "Ai:Anthropic";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "claude-sonnet-5";
    public int MaxTokens { get; set; } = 2048;
}
