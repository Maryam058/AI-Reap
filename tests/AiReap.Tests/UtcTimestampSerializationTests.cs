using System.Globalization;
using System.Text.Json.Nodes;
using AiReap.Domain.Common;
using Xunit;
using static AiReap.Tests.LifecycleTestSupport;

namespace AiReap.Tests;

// Timestamps are stored as UTC in datetime2 columns, which EF reads back with DateTimeKind.Unspecified.
// The API must still emit them as explicit UTC instants ("...Z"); without the marker a browser reads
// them as local time and every "5 minutes ago" is off by the viewer's UTC offset.
public class UtcTimestampSerializationTests : IAsyncLifetime
{
    private readonly TestHost _host = new();
    private Actor _ba = null!;

    public async Task InitializeAsync() => _ba = await _host.ActorAsync(Roles.BusinessAnalyst);

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    [Fact]
    public async Task Timestamps_reloaded_from_the_database_are_serialized_as_explicit_utc()
    {
        var before = DateTimeOffset.UtcNow.AddMinutes(-5);
        var (created, project) = await _ba.Api.PostAsync("/api/projects", new
        {
            name = "Room booking",
            businessProblem = "Double bookings",
            objectives = "No double bookings",
            scope = "Staff bookings"
        });
        Assert.Equal(201, created);
        var id = Id(project);
        var (moved, _) = await _ba.Api.PostAsync($"/api/projects/{id}/lifecycle/transitions", new { targetStatus = 1 });
        Assert.Equal(201, moved);

        var stamps = new List<(string Path, string Value)>();
        foreach (var path in new[] { $"/api/projects/{id}", $"/api/projects/{id}/lifecycle", $"/api/projects/{id}/lifecycle/history", "/api/notifications" })
        {
            var (status, body) = await _ba.Api.GetAsync(path);
            Assert.Equal(200, status);
            Collect(body, path, stamps);
        }

        Assert.Contains(stamps, s => s.Path.EndsWith(".updatedAt"));
        Assert.Contains(stamps, s => s.Path.EndsWith(".requestedAt"));
        Assert.Contains(stamps, s => s.Path.StartsWith("/api/notifications") && s.Path.EndsWith(".createdAt"));
        foreach (var (path, value) in stamps)
        {
            Assert.True(value.EndsWith('Z'), $"{path} = {value} has no UTC marker");
            var instant = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
            Assert.InRange(instant, before, DateTimeOffset.UtcNow.AddMinutes(5)); // same instant, not shifted
        }
    }

    // Every string property named "...At" anywhere in the response (decidedAt etc. may be null).
    private static void Collect(JsonNode? node, string path, List<(string, string)> into)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key.EndsWith("At") && value is JsonValue v && v.TryGetValue<string>(out var s)) into.Add(($"{path}.{key}", s));
                    else Collect(value, $"{path}.{key}", into);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++) Collect(arr[i], $"{path}[{i}]", into);
                break;
        }
    }
}
