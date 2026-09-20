using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiReap.Application.Artifacts;

// Artifact payloads are stored with the C# property names (PascalCase), but the rest of the API
// — and the web client's types — use camelCase. ASP.NET's naming policy doesn't rename the keys
// inside a raw JsonElement, so artifact `data` is normalised here on the way out. Storage and
// server-side reads (which are case-insensitive) are untouched.
public static class ArtifactJson
{
    public static JsonElement ToCamelCaseElement(string json) =>
        JsonSerializer.SerializeToElement(ConvertNode(JsonNode.Parse(json)));

    private static JsonNode? ConvertNode(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(kv =>
            new KeyValuePair<string, JsonNode?>(JsonNamingPolicy.CamelCase.ConvertName(kv.Key), ConvertNode(kv.Value)))),
        JsonArray arr => new JsonArray(arr.Select(ConvertNode).ToArray()),
        _ => node?.DeepClone()
    };
}
