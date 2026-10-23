using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Domain.Enums;

namespace AiReap.Application.Artifacts;

// P0-2 — a generated FR/NFR's evidence fields, edited as a JSON object (like NfrAssumption) so no
// other field is touched. Stored keys may be PascalCase (generation) or camelCase (human edits);
// every lookup ignores case.
//
// Edits (D6): the quotes and every result the server computed are read-only for humans - whatever
// the client sends, they are restored from the previous version. UnsupportedNumbers and
// TargetSupport are recalculated from the edited claim text against those same verified quotes, so
// an edit can neither keep a stale warning nor forge "quote found". requirementOrigin,
// derivationNote, assumedClarifications and targetOrigin stay editable (a versioned human decision).
// A record made before evidence checks (no EvidenceCheck) gets none of these fields from an edit.
//
// Reads (S2): a caller who may not read raw sources (a Developer) never receives quote text - nor
// the document label of an excerpt quote - only each quote's source kind, statuses and labels.
public static class RequirementEvidenceFields
{
    private const string EvidenceKey = "Evidence";
    private const string StatusKey = "EvidenceStatus";
    private const string NumbersKey = "UnsupportedNumbers";
    private const string CheckKey = "EvidenceCheck";
    private const string TargetSupportKey = "TargetSupport";
    private const string QuoteKey = "Quote";
    private const string RefKey = "Ref";
    private const string FromKey = "From";

    private static readonly string[] ServerOwned = [EvidenceKey, StatusKey, NumbersKey, CheckKey, TargetSupportKey];

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static bool Applies(ArtifactType type) =>
        type is ArtifactType.FunctionalRequirement or ArtifactType.NonFunctionalRequirement;

    // Whether this edit touches the evidence fields at all: the previous version was evidence-checked,
    // or the client sent one of the server-owned keys. When neither, the edit is stored as before.
    public static bool Involved(string previousJson, JsonObject edited) =>
        Lookup(ParseObject(previousJson), CheckKey) is not null || ServerOwned.Any(k => Lookup(edited, k) is not null);

    // The edited payload with the server-owned fields decided here (see above). Mutates and returns it.
    public static JsonObject SanitizeEdit(string previousJson, JsonObject edited, ArtifactType type)
    {
        var previous = ParseObject(previousJson);
        foreach (var key in ServerOwned)
        {
            Remove(edited, key);
        }

        if (Lookup(previous, CheckKey) is null)
        {
            return edited; // made before evidence checks: nothing to restore, nothing invented
        }

        foreach (var key in new[] { EvidenceKey, StatusKey, CheckKey })
        {
            if (Lookup(previous, key) is { } value) edited[key] = value.DeepClone();
        }

        var quotes = Lookup(previous, EvidenceKey) is JsonArray array
            ? array.Deserialize<List<EvidenceQuotePayload>>(ReadOptions) ?? []
            : [];
        var claim = type == ArtifactType.FunctionalRequirement
            ? RequirementEvidence.FunctionalClaim(Text(edited, "Preconditions"), Text(edited, "Inputs"), Text(edited, "Processing"), Text(edited, "ExpectedResult"))
            : RequirementEvidence.NonFunctionalClaim(Text(edited, "Description"), Text(edited, "TargetValue"));
        edited[NumbersKey] = new JsonArray(RequirementEvidence.UnsupportedNumbers(claim, quotes).Select(n => (JsonNode)n).ToArray());

        if (type == ArtifactType.NonFunctionalRequirement)
        {
            edited[TargetSupportKey] = RequirementEvidence.TargetSupportFor(Text(edited, "TargetOrigin"), Text(edited, "TargetValue"), quotes);
        }

        return edited;
    }

    // The payload as a restricted caller may see it: each evidence quote without its text (and an
    // excerpt quote without its document label). Anything that is not an evidence-carrying payload
    // is returned unchanged.
    // Also removes a clarification question's source references, evidence quotes and related document
    // passages (ClarificationSourceReferences.SourceTextKeys): they are source text, and a restricted
    // caller cannot open the source anyway.
    public static string RedactForRestrictedCaller(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject payload)
        {
            return json;
        }

        // A clarification question's evidence quotes and related document passages are source text too.
        var hasReferences = false;
        foreach (var key in ClarificationSourceReferences.SourceTextKeys)
        {
            if (Lookup(payload, key) is not null)
            {
                Remove(payload, key);
                hasReferences = true;
            }
        }

        if (Lookup(payload, EvidenceKey) is not JsonArray evidence)
        {
            return hasReferences ? payload.ToJsonString() : json;
        }

        foreach (var quote in evidence.OfType<JsonObject>())
        {
            var isExcerpt = string.Equals(Text(quote, FromKey)?.Trim(), RequirementEvidence.FromExcerpt, StringComparison.OrdinalIgnoreCase);
            Remove(quote, QuoteKey);
            if (isExcerpt) Remove(quote, RefKey);
            quote["QuoteWithheld"] = true;
        }

        return payload.ToJsonString();
    }

    private static JsonObject ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
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

    private static string? Text(JsonObject payload, string key) => Lookup(payload, key) switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        null => null,
        var other => other.ToJsonString()
    };
}
