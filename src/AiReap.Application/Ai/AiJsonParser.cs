using System.Text.Json;

namespace AiReap.Application.Ai;

// Strict JSON parsing for AI responses (§31: "structured JSON output is mandatory...
// validate AI responses before persistence"). Models occasionally wrap JSON in markdown
// code fences despite instructions not to — that's stripped, but the payload itself must
// still deserialize cleanly or the whole response is rejected (REAP-005), never patched
// up or partially accepted.
public static class AiJsonParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static T Parse<T>(string rawResponse) where T : class
    {
        var json = StripMarkdownFence(rawResponse);

        try
        {
            var result = JsonSerializer.Deserialize<T>(json, Options);
            if (result is null)
            {
                throw new AiOutputValidationException("AI response deserialized to null.");
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new AiOutputValidationException(
                $"AI response was not valid JSON in the expected shape: {ex.Message}", ex);
        }
    }

    private static string StripMarkdownFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```"))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        var withoutOpeningFence = firstNewline >= 0 ? trimmed[(firstNewline + 1)..] : trimmed;

        var closingFenceIndex = withoutOpeningFence.LastIndexOf("```", StringComparison.Ordinal);
        return closingFenceIndex >= 0 ? withoutOpeningFence[..closingFenceIndex].Trim() : withoutOpeningFence.Trim();
    }
}
