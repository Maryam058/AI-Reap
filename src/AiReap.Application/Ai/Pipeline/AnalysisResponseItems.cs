using System.Text.Json;

namespace AiReap.Application.Ai.Pipeline;

// One missingInformation item as the analysis model returned it, read back from the raw response kept in
// its AIExecution audit row (OutputJson). Quotes: its "evidence" (RequirementAnalysis-v3) and the "quote"
// of each claimed source reference (v2 onwards) - the model's words, to be verified before any is used.
public sealed record GeneratedClarification(
    string Question, string? Reason, IReadOnlyList<string> Quotes, IReadOnlyList<ClaimedSourceReference> Claims);

// Reads the questions out of a RequirementAnalysis raw response: one response, possibly fenced, or the
// merged form of a split source ({"parts": [{"response": "..."}]}, RequirementAnalysisService.Merge).
// A response that does not parse yields nothing; nothing here is trusted until it is verified.
public static class AnalysisResponseItems
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static List<GeneratedClarification> Parse(string? outputJson)
    {
        var items = new List<GeneratedClarification>();
        if (Root(outputJson) is not { } root)
        {
            return items;
        }

        if (Property(root, "parts") is { ValueKind: JsonValueKind.Array } parts)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (Property(part, "response") is { ValueKind: JsonValueKind.String } response)
                {
                    items.AddRange(Parse(response.GetString()));
                }
            }

            return items;
        }

        if (Property(root, "missingInformation") is not { ValueKind: JsonValueKind.Array } questions)
        {
            return items;
        }

        foreach (var element in questions.EnumerateArray())
        {
            Item? item;
            try
            {
                item = element.Deserialize<Item>(Options);
            }
            catch (JsonException)
            {
                continue;
            }

            if (item is null || string.IsNullOrWhiteSpace(item.Question))
            {
                continue;
            }

            var claims = item.SourceRefs ?? [];
            var quotes = RequirementAnalysisService.EvidenceQuotes(item.Evidence)
                .Concat(claims.Select(c => c.Quote).OfType<string>())
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            items.Add(new GeneratedClarification(item.Question, item.Reason, quotes, claims));
        }

        return items;
    }

    private static JsonElement? Root(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = trimmed.IndexOf('\n');
            trimmed = newline >= 0 ? trimmed[(newline + 1)..] : trimmed;
            var fence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            trimmed = fence >= 0 ? trimmed[..fence] : trimmed;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private sealed class Item
    {
        public string Question { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public List<JsonElement>? Evidence { get; set; }
        public List<ClaimedSourceReference>? SourceRefs { get; set; }
    }
}
