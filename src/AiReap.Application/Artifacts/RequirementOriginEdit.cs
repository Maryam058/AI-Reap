using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Common;
using AiReap.Domain.Enums;

namespace AiReap.Application.Artifacts;

// G3 follow-ups on a human edit of a functional or non-functional requirement, checked on the data
// exactly as sent and before anything is changed, so a refused edit touches nothing.
//
// D1 — the data must be a JSON object. Anything else (array, string, number, boolean, null) would be
// stored as sent and then read by RequirementApprovalEvidence as an empty object: an evidence-checked
// requirement would turn into an unchecked one that no longer blocks.
//
// P3 — a requirement whose origin is derived or assumed must carry a derivation note (not blank,
// at most MaxDerivationNoteLength characters). A note is required when the edit reclassifies the
// requirement (its origin changes to derived/assumed, or between the two) and when it would blank a
// note the previous version had. An unchanged derived/assumed origin that never had a note (as some
// generated requirements are) is left as it is: nothing is generated or inferred for it.
//
// Both keys are looked up ignoring case, like every server read of a payload. A key that appears
// more than once (in any casing) or is not a string is refused, so the value judged here is the one
// every later reader sees.
public static class RequirementOriginEdit
{
    public const int MaxDerivationNoteLength = 2000;

    private const string OriginKey = "RequirementOrigin";
    private const string NoteKey = "DerivationNote";
    private const string Derived = "derived", Assumed = "assumed";

    public static void Validate(ArtifactType type, string previousJson, JsonElement edited)
    {
        if (edited.ValueKind != JsonValueKind.Object)
        {
            throw new RequestValidationException(type == ArtifactType.NonFunctionalRequirement
                ? "Non-functional requirement data must be a JSON object."
                : "Functional requirement data must be a JSON object.");
        }

        var origin = Normalize(SingleText(edited, OriginKey));
        var note = SingleText(edited, NoteKey);
        var previous = ParseObject(previousJson);
        var previousNote = Text(previous, NoteKey);

        if (note is not null && note != previousNote && note.Trim().Length > MaxDerivationNoteLength)
        {
            throw new RequestValidationException($"A derivation note can be at most {MaxDerivationNoteLength} characters.");
        }

        if (origin is not (Derived or Assumed) || !string.IsNullOrWhiteSpace(note))
        {
            return;
        }

        var unchangedWithoutNote = origin == Normalize(Text(previous, OriginKey)) && string.IsNullOrWhiteSpace(previousNote);
        if (!unchangedWithoutNote)
        {
            throw new RequestValidationException(
                $"A derivation note is required when a requirement's origin is {origin}: say how it follows from the source, or what it assumes.");
        }
    }

    // The key's string value (null when absent or JSON null); refused when repeated or not a string.
    private static string? SingleText(JsonElement payload, string key)
    {
        var matches = payload.EnumerateObject().Where(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1)
        {
            throw new RequestValidationException($"\"{key}\" appears more than once in the requirement data.");
        }

        return matches.Count == 0 ? null : matches[0].Value.ValueKind switch
        {
            JsonValueKind.String => matches[0].Value.GetString(),
            JsonValueKind.Null => null,
            _ => throw new RequestValidationException($"\"{key}\" must be a string.")
        };
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

    private static string? Text(JsonObject payload, string key) =>
        payload.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value is JsonValue value
        && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
