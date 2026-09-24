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

    public static T Parse<T>(string rawResponse, IEnumerable<string>? knownArtifactCodes = null,
        IEnumerable<string>? knownSourceReferences = null) where T : class
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            throw new AiOutputValidationException("AI response was empty.");
        }

        var json = StripMarkdownFence(rawResponse);

        T? result;
        try
        {
            result = JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new AiOutputValidationException(
                $"AI response was not valid JSON in the expected shape: {ex.Message}", ex);
        }

        if (result is null)
        {
            throw new AiOutputValidationException("AI response deserialized to null.");
        }

        // §31 — semantic checks (required fields, allowed values, real artifact references).
        if (result is IValidatableAiResponse validatable)
        {
            var validator = new AiResponseValidator(knownArtifactCodes, knownSourceReferences);
            validatable.Validate(validator);
            if (!validator.IsValid)
            {
                throw new AiOutputValidationException(
                    $"AI response failed validation: {string.Join(" ", validator.Errors)}", validator.Errors);
            }
        }

        return result;
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
