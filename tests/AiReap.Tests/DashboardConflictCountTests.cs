using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// §28/§15 - the Dashboard's conflict-count metric reports duplicate/conflict pairs that
// ConflictDetectionService has already recorded as ArtifactRelationships; it never re-runs
// detection itself. The stub AI client always returns an empty findings list (see
// StubAiChatClient.ConflictsResponse), so a real relationship is seeded directly here rather
// than driven through /detect-conflicts, to isolate what this metric is actually responsible for.
public class DashboardConflictCountTests : IAsyncLifetime
{
    private readonly TestHost _host = new();
    private ApiUser _ba = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.RegisterAsync("BusinessAnalyst");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();

    private async Task<string> NewFunctionalRequirementAsync(string projectId)
    {
        var (ss, source) = await _ba.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve." });
        Assert.InRange(ss, 200, 201);
        var (gs, generated) = await _ba.PostAsync($"/api/requirement-sources/{Id(source)}/generate-requirements");
        Assert.Equal(200, gs);
        return Id(generated!["functionalRequirements"]![0]);
    }

    [Fact]
    public async Task Dashboard_reports_recorded_conflicts_and_duplicates_without_double_counting_relationship_direction()
    {
        var (ps, project) = await _ba.PostAsync("/api/projects", new { name = "Conflicts", description = "d" });
        Assert.Equal(201, ps);
        var projectId = Id(project);

        var frA = await NewFunctionalRequirementAsync(projectId);
        var frB = await NewFunctionalRequirementAsync(projectId);
        var frC = await NewFunctionalRequirementAsync(projectId);

        var (baselineStatus, baseline) = await _ba.GetAsync($"/api/projects/{projectId}/dashboard");
        Assert.Equal(200, baselineStatus);
        Assert.Equal(0, baseline!["conflictCount"]!.GetValue<int>());

        // RelationshipType: ConflictsWith = 4, DuplicateOf = 5 (see Domain/Enums/RelationshipType.cs).
        await _host.ExecuteAsync($"""
            INSERT INTO ArtifactRelationships (Id, SourceArtifactId, TargetArtifactId, RelationshipType, CreatedAt)
            VALUES (NEWID(), '{frA}', '{frB}', 4, GETUTCDATE())
            """);

        var (afterOneStatus, afterOne) = await _ba.GetAsync($"/api/projects/{projectId}/dashboard");
        Assert.Equal(200, afterOneStatus);
        Assert.Equal(1, afterOne!["conflictCount"]!.GetValue<int>());

        await _host.ExecuteAsync($"""
            INSERT INTO ArtifactRelationships (Id, SourceArtifactId, TargetArtifactId, RelationshipType, CreatedAt)
            VALUES (NEWID(), '{frB}', '{frC}', 5, GETUTCDATE())
            """);

        var (afterTwoStatus, afterTwo) = await _ba.GetAsync($"/api/projects/{projectId}/dashboard");
        Assert.Equal(200, afterTwoStatus);
        Assert.Equal(2, afterTwo!["conflictCount"]!.GetValue<int>());

        // A non-conflict relationship type (e.g. DerivedFrom = 0) must not be counted.
        await _host.ExecuteAsync($"""
            INSERT INTO ArtifactRelationships (Id, SourceArtifactId, TargetArtifactId, RelationshipType, CreatedAt)
            VALUES (NEWID(), '{frA}', '{frC}', 0, GETUTCDATE())
            """);

        var (afterThreeStatus, afterThree) = await _ba.GetAsync($"/api/projects/{projectId}/dashboard");
        Assert.Equal(200, afterThreeStatus);
        Assert.Equal(2, afterThree!["conflictCount"]!.GetValue<int>());
    }
}
