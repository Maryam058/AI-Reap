using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// §28 / REAP-083 - the Dashboard's test-coverage and requirements-needing-review metrics.
// Test coverage keeps the app's one definition (coverage.ts, traceability matrix): FRs with at least
// one TestedBy test case / all FRs, every status - a rejected FR is not silently dropped. Unlike the
// matrix, the dashboard figures are project-wide aggregates, identical for every role, and never name
// an artifact. "Requirements needing review" counts FRs and NFRs only; PendingReviewCount keeps its
// meaning (every artifact type in AiGenerated/Draft/UnderReview). Statuses are set directly in SQL so
// the fixture is exact - this metric only reports what is stored.
public class DashboardCoverageAndReviewTests : IAsyncLifetime
{
    private const int AiGenerated = 0, Draft = 1, UnderReview = 2, Approved = 3, Rejected = 4;
    private const int FunctionalRequirement = 0, NonFunctionalRequirement = 1, UserStory = 3, TestCase = 10;

    private readonly TestHost _host = new();
    private ApiUser _ba = null!, _dev = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.RegisterAsync("BusinessAnalyst");
        _dev = await _host.RegisterAsync("Developer");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();

    private sealed record Fixture(string ProjectId, string[] Frs, string[] Nfrs, IReadOnlyDictionary<string, string> Codes);

    // Five sources: the stub AI yields one FR and one NFR per generation. FR1 and FR3 get test cases.
    private async Task<Fixture> BuildAsync()
    {
        var (ps, project) = await _ba.PostAsync("/api/projects", new { name = "Coverage", description = "d" });
        Assert.Equal(201, ps);
        var projectId = Id(project);
        Assert.InRange((await _host.AddMemberAsync(_ba, projectId, _dev)).Status, 200, 201);

        string? firstSource = null;
        for (var i = 0; i < 5; i++)
        {
            var (ss, source) = await _ba.PostAsync($"/api/projects/{projectId}/requirement-sources",
                new { sourceType = 1, rawText = $"Employees submit leave requests; managers approve them. ({i})" });
            Assert.InRange(ss, 200, 201);
            firstSource ??= Id(source);
            Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{Id(source)}/generate-requirements")).Status);
        }
        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{firstSource}/generate-user-stories")).Status);

        var all = (await _ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray();
        string[] Of(int type) => all.Where(a => a!["artifactType"]!.GetValue<int>() == type)
            .OrderBy(a => a!["code"]!.GetValue<string>()).Select(Id).ToArray();
        var frs = Of(FunctionalRequirement);
        var nfrs = Of(NonFunctionalRequirement);
        Assert.Equal(5, frs.Length);
        Assert.Equal(5, nfrs.Length);

        Assert.Equal(200, (await _ba.PostAsync($"/api/artifacts/{frs[0]}/generate-test-cases")).Status);
        Assert.Equal(200, (await _ba.PostAsync($"/api/artifacts/{frs[2]}/generate-test-cases")).Status);

        var codes = (await _ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray()
            .ToDictionary(a => Id(a), a => a!["code"]!.GetValue<string>());

        // Baseline: everything Approved, then the exact mixture under test.
        await _host.ExecuteAsync($"UPDATE Artifacts SET Status = {Approved} WHERE ProjectId = '{projectId}'");
        async Task Set(string id, int status) =>
            await _host.ExecuteAsync($"UPDATE Artifacts SET Status = {status} WHERE Id = '{id}'");
        int[] statuses = [AiGenerated, Draft, Rejected, UnderReview, Approved];
        for (var i = 0; i < 5; i++)
        {
            await Set(frs[i], statuses[i]);          // FR1 AiGenerated (tested), FR2 Draft, FR3 Rejected (tested), FR4 UnderReview, FR5 Approved
            await Set(nfrs[i], statuses[i]);         // NFR1 AiGenerated, NFR2 Draft, NFR3 Rejected, NFR4 UnderReview, NFR5 Approved
        }
        // Non-requirement artifacts in review-like statuses must not count as requirements needing review.
        await _host.ExecuteAsync($"UPDATE Artifacts SET Status = {UnderReview} WHERE ProjectId = '{projectId}' AND ArtifactType = {TestCase}");
        await _host.ExecuteAsync($"UPDATE Artifacts SET Status = {Draft} WHERE ProjectId = '{projectId}' AND ArtifactType = {UserStory}");
        await _host.ExecuteAsync($"UPDATE Artifacts SET Status = {AiGenerated} WHERE ProjectId = '{projectId}' AND ArtifactType NOT IN ({FunctionalRequirement}, {NonFunctionalRequirement}, {TestCase}, {UserStory})");

        return new Fixture(projectId, frs, nfrs, codes);
    }

    [Fact]
    public async Task Test_coverage_counts_frs_with_a_tested_by_test_case_over_all_frs_including_rejected()
    {
        var f = await BuildAsync();

        var (s, dash) = await _ba.GetAsync($"/api/projects/{f.ProjectId}/dashboard");
        Assert.Equal(200, s);

        // FR1 (AiGenerated) and FR3 (Rejected) are tested; the rejected one is not excluded.
        Assert.Equal(2, dash!["testedFunctionalRequirementCount"]!.GetValue<int>());
        Assert.Equal(5, dash["functionalRequirementCount"]!.GetValue<int>());

        // Same definition as the BA's (unfiltered) traceability matrix.
        var matrix = (await _ba.GetAsync($"/api/projects/{f.ProjectId}/traceability-matrix")).Body!.AsArray();
        Assert.Equal(matrix.Count, dash["functionalRequirementCount"]!.GetValue<int>());
        Assert.Equal(matrix.Count(r => r!["testCases"]!.AsArray().Count > 0), dash["testedFunctionalRequirementCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Requirements_needing_review_counts_only_frs_and_nfrs_in_review_statuses_and_pending_review_is_unchanged()
    {
        var f = await BuildAsync();

        var (s, dash) = await _ba.GetAsync($"/api/projects/{f.ProjectId}/dashboard");
        Assert.Equal(200, s);

        // FR1-2, FR4 and NFR1-2, NFR4: AiGenerated/Draft/UnderReview. Approved, rejected, test cases,
        // stories and every other non-requirement artifact are excluded.
        Assert.Equal(6, dash!["requirementsNeedingReviewCount"]!.GetValue<int>());

        // PendingReviewCount keeps its meaning: every artifact type in those statuses.
        var expectedPending = await _host.ScalarAsync($"""
            SELECT COUNT(*) FROM Artifacts
            WHERE ProjectId = '{f.ProjectId}' AND Status IN ({AiGenerated}, {Draft}, {UnderReview})
            """);
        Assert.Equal(expectedPending, dash["pendingReviewCount"]!.GetValue<int>());
        Assert.True(expectedPending > 6, "fixture must include non-requirement artifacts awaiting review");
    }

    [Fact]
    public async Task Developer_receives_the_same_aggregate_counts_without_hidden_artifact_details()
    {
        var f = await BuildAsync();

        var (bs, baDash) = await _ba.GetAsync($"/api/projects/{f.ProjectId}/dashboard");
        var (ds, devDash) = await _dev.GetAsync($"/api/projects/{f.ProjectId}/dashboard");
        Assert.Equal(200, bs);
        Assert.Equal(200, ds);

        foreach (var count in new[] { "functionalRequirementCount", "testedFunctionalRequirementCount",
                     "requirementsNeedingReviewCount", "pendingReviewCount" })
        {
            Assert.Equal(JsonValueKind.Number, devDash![count]!.GetValueKind());
            Assert.Equal(baDash![count]!.GetValue<int>(), devDash[count]!.GetValue<int>());
        }
        Assert.Equal(2, devDash!["testedFunctionalRequirementCount"]!.GetValue<int>());
        Assert.Equal(6, devDash["requirementsNeedingReviewCount"]!.GetValue<int>());

        // The Developer's own matrix only shows the approved FR; the dashboard still reports project-wide.
        var devMatrix = (await _dev.GetAsync($"/api/projects/{f.ProjectId}/traceability-matrix")).Body!.AsArray();
        Assert.Single(devMatrix);

        // Only numbers: no unapproved FR/NFR is named anywhere in the Developer's dashboard.
        var devJson = devDash.ToJsonString();
        foreach (var hidden in f.Frs.Take(4).Concat(f.Nfrs.Take(4)))
        {
            Assert.DoesNotContain(f.Codes[hidden], devJson);
            Assert.DoesNotContain(hidden, devJson);
        }
    }
}
