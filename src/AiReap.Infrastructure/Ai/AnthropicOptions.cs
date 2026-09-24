namespace AiReap.Infrastructure.Ai;

public class AnthropicOptions
{
    public const string SectionName = "Ai:Anthropic";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "claude-sonnet-5";

    // 2048 was too low in practice: a large uploaded requirement source (e.g. a ~68K-character
    // policy document) produces a long structured-extraction response (actors/capabilities/
    // notes/missingInformation covering the whole document) that got cut off mid-JSON at 2048
    // tokens, which then failed AiJsonParser's strict parse and surfaced as an opaque 500. 8192
    // gives real headroom for that case while staying well short of the model's actual output
    // ceiling.
    public int MaxTokens { get; set; } = 8192;

    // A blank ApiKey is an obvious "not configured" signal, but a leftover placeholder (e.g. the
    // literal "sk-ant-..." some setup docs show as an example) is not blank and would otherwise
    // pass a null/whitespace check straight through to a real HTTP call that's guaranteed to fail
    // with 401. Real Anthropic keys are long (100+ characters) and never contain "..." - anything
    // shorter or containing it is treated as unconfigured so callers can fall back safely instead.
    private const int MinimumPlausibleKeyLength = 40;

    public bool HasUsableApiKey =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !ApiKey.Contains("...", StringComparison.Ordinal)
        && ApiKey.Trim().Length >= MinimumPlausibleKeyLength;
}
