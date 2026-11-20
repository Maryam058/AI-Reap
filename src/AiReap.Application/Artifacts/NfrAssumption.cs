using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Common;

namespace AiReap.Application.Artifacts;

// REAP-011/REAP-045 — an NFR's AssumptionStatus (and who confirmed it, and when) is server-owned.
// Payload JSON is edited as a JSON object rather than round-tripped through
// NonFunctionalRequirementPayload, so no other field is touched. Stored keys may be PascalCase
// (generation) or camelCase (human edits); lookups are case-insensitive, like every server read.
public static class NfrAssumption
{
    public const string Confirmed = "confirmed";
    public const string Proposed = "proposed_assumption";

    private const string StatusKey = "AssumptionStatus";
    private const string ConfirmedByKey = "ConfirmedByUserId";
    private const string ConfirmedAtKey = "ConfirmedAt";
    private const string TargetKey = "TargetValue";

    public static string? TargetOf(string json) => Normalize(Get(ParseObject(json), TargetKey));

    public static bool IsConfirmed(string json) => Get(ParseObject(json), StatusKey) == Confirmed;

    // A human edit's payload with the server-owned fields decided here, whatever the client sent:
    // no target -> null; the same target on an already confirmed NFR -> stays confirmed (with its
    // original confirmer and time); anything else -> proposed_assumption.
    public static JsonObject SanitizeEdit(string previousJson, JsonElement edited)
    {
        var next = JsonNode.Parse(edited.GetRawText()) as JsonObject
            ?? throw new RequestValidationException("Non-functional requirement data must be a JSON object.");
        var previous = ParseObject(previousJson);

        var target = Normalize(Get(next, TargetKey));
        var keepConfirmation = target is not null
            && Get(previous, StatusKey) == Confirmed
            && target == Normalize(Get(previous, TargetKey));

        RemoveServerOwned(next);
        if (target is null)
        {
            next[StatusKey] = null;
        }
        else if (keepConfirmation)
        {
            next[StatusKey] = Confirmed;
            next[ConfirmedByKey] = Lookup(previous, ConfirmedByKey)?.DeepClone();
            next[ConfirmedAtKey] = Lookup(previous, ConfirmedAtKey)?.DeepClone();
        }
        else
        {
            next[StatusKey] = Proposed;
        }

        return next;
    }

    // The payload with only the confirmation fields set; everything else is kept as stored.
    public static string Confirm(string json, string userId, DateTime confirmedAt)
    {
        var payload = ParseObject(json);
        RemoveServerOwned(payload);
        payload[StatusKey] = Confirmed;
        payload[ConfirmedByKey] = userId;
        payload[ConfirmedAtKey] = confirmedAt;
        return payload.ToJsonString();
    }

    private static JsonObject ParseObject(string json) => JsonNode.Parse(json) as JsonObject ?? new JsonObject();

    private static void RemoveServerOwned(JsonObject payload)
    {
        var keys = payload.Select(kv => kv.Key)
            .Where(k => k.Equals(StatusKey, StringComparison.OrdinalIgnoreCase)
                        || k.Equals(ConfirmedByKey, StringComparison.OrdinalIgnoreCase)
                        || k.Equals(ConfirmedAtKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in keys)
        {
            payload.Remove(key);
        }
    }

    private static JsonNode? Lookup(JsonObject payload, string key) =>
        payload.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? Get(JsonObject payload, string key) => Lookup(payload, key) switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        null => null,
        var other => other.ToJsonString()
    };

    private static string? Normalize(string? target) => string.IsNullOrWhiteSpace(target) ? null : target.Trim();
}
