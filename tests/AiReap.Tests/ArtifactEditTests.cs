using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// §23 — the human-edit endpoint (PATCH /api/artifacts/{id}) that the new ArtifactEditDialog
// frontend form calls. There's no frontend test runner in this repo yet, so this covers the
// capability the UI depends on: round-tripping the trickier payload shapes the edit form
// handles specially (DataEntity's array-of-objects Fields, TestCase's string-list Steps),
// not just a flat string field (already covered by Section37DemoTests' title edit).
public class ArtifactEditTests : IAsyncLifetime
{
    private readonly TestHost _host = new();
    private ApiUser _ba = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.RegisterAsync("BusinessAnalyst");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();

    private async Task<string> SourceWithFunctionalRequirementAsync()
    {
        var (ps, p) = await _ba.PostAsync("/api/projects", new { name = "Edit", description = "d" });
        Assert.Equal(201, ps);
        var (ss, src) = await _ba.PostAsync($"/api/projects/{Id(p)}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve." });
        Assert.InRange(ss, 200, 201);
        var sourceId = Id(src);
        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements")).Status);
        return sourceId;
    }

    [Fact]
    public async Task Editing_a_data_entity_round_trips_the_fields_array_correctly()
    {
        var sourceId = await SourceWithFunctionalRequirementAsync();
        var (des, entities) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-data-entities");
        Assert.Equal(200, des);
        var entityId = Id(entities![0]);

        var newData = new
        {
            fields = new object[]
            {
                new { name = "Id", dataType = "Guid", required = true },
                new { name = "Notes", dataType = "string", required = false },
            },
            keys = new[] { "Id" },
            relationships = "One Request has many Notes",
            indexes = new[] { "Notes" },
        };

        var (patchStatus, updated) = await _ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{entityId}",
            new { data = newData, reason = "Added a Notes field" });
        Assert.Equal(200, patchStatus);

        var fields = updated!["data"]!["fields"]!.AsArray();
        Assert.Equal(2, fields.Count);
        Assert.Equal("Notes", fields[1]!["name"]!.GetValue<string>());
        Assert.Equal("string", fields[1]!["dataType"]!.GetValue<string>());
        Assert.False(fields[1]!["required"]!.GetValue<bool>());
        Assert.Equal("One Request has many Notes", updated["data"]!["relationships"]!.GetValue<string>());
        Assert.Equal(2, updated["currentVersion"]!.GetValue<int>());

        // fetching fresh confirms it persisted, not just echoed back in the response
        var refetched = (await _ba.GetAsync($"/api/artifacts/{entityId}")).Body!;
        Assert.Equal(2, refetched["data"]!["fields"]!.AsArray().Count);
    }

    [Fact]
    public async Task Editing_a_test_case_round_trips_the_steps_list_and_kind()
    {
        var sourceId = await SourceWithFunctionalRequirementAsync();
        var (gen, requirements) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        Assert.Equal(200, gen);
        var frId = Id(requirements!["functionalRequirements"]![0]);
        var (tcs, cases) = await _ba.PostAsync($"/api/artifacts/{frId}/generate-test-cases");
        Assert.Equal(200, tcs);
        var caseId = Id(cases![0]);

        var newData = new
        {
            preconditions = "User is authenticated and has zero balance",
            steps = new[] { "Open the request form", "Attempt to submit with no balance", "Observe the error" },
            expectedResult = "Submission is blocked with a clear balance error",
            testKind = "negative",
        };

        var (patchStatus, updated) = await _ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{caseId}",
            new { data = newData, reason = "Corrected this to a negative-path test" });
        Assert.Equal(200, patchStatus);

        var steps = updated!["data"]!["steps"]!.AsArray();
        Assert.Equal(3, steps.Count);
        Assert.Equal("Observe the error", steps[2]!.GetValue<string>());
        Assert.Equal("negative", updated["data"]!["testKind"]!.GetValue<string>());

        var versions = (await _ba.GetAsync($"/api/artifacts/{caseId}/versions")).Body!.AsArray();
        Assert.Equal(2, versions.Count);
        Assert.Equal(1, versions.Count(v => v!["origin"]!.GetValue<int>() == 1)); // one human-origin version: the edit
    }
}
