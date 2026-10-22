using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai;
using AiReap.Application.Artifacts.Payloads;

namespace AiReap.Application.Artifacts;

// A clarification question's SourceReferences (SourceReferenceIndex), edited as a JSON object so no
// other field is touched. Stored keys may be PascalCase (generation) or camelCase (human edits).
//
// Reads: a question saved before references were resolved has none stored; they are resolved from its
// Reason against its source when it is read, and not saved. Restricted callers (S2) never get them -
// RequirementEvidenceFields.RedactForRestrictedCaller removes them with the other source quotes.
// Edits: server-owned - whatever the client sends, the previous version's references are kept.
public static class ClarificationSourceReferences
{
    public const string Key = nameof(ClarificationQuestionPayload.SourceReferences);
    private const string ReasonKey = nameof(ClarificationQuestionPayload.Reason);

    // The question's data with its references, resolved now when none were stored.
    public static string WithReferences(string dataJson, SourceReferenceIndex index)
    {
        if (Parse(dataJson) is not { } payload || Lookup(payload, Key) is JsonArray)
        {
            return dataJson;
        }

        var reason = Lookup(payload, ReasonKey) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        Remove(payload, Key);
        payload[Key] = JsonSerializer.SerializeToNode(index.Resolve(reason));
        return payload.ToJsonString();
    }

    public static JsonObject KeepOnEdit(string previousJson, JsonObject edited)
    {
        Remove(edited, Key);
        if (Parse(previousJson) is { } previous && Lookup(previous, Key) is { } kept)
        {
            edited[Key] = kept.DeepClone();
        }

        return edited;
    }

    private static JsonObject? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Remove(JsonObject payload, string key)
    {
        foreach (var k in payload.Select(kv => kv.Key).Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            payload.Remove(k);
        }
    }

    private static JsonNode? Lookup(JsonObject payload, string key) =>
        payload.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
}
