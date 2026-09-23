using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// §8 — the Open/Answered/Resolved/NotApplicable clarification lifecycle. Open->Answered and
// Open->NotApplicable were already covered elsewhere (RegressionTests); this covers the
// previously-unwired Answered->Resolved transition (ArtifactService.ResolveClarificationIfAnsweredAsync)
// and its guard against resolving a NotApplicable or still-Open question.
public class ClarificationResolutionTests : IAsyncLifetime
{
    private readonly TestHost _host = new();
    private ApiUser _ba = null!, _reviewer = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.RegisterAsync("BusinessAnalyst");
        _reviewer = await _host.RegisterAsync("Reviewer");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();

    private async Task<(string ProjectId, string SourceId)> ProjectWithSourceAsync()
    {
        var (s, p) = await _ba.PostAsync("/api/projects", new { name = "Clarify", description = "d" });
        Assert.Equal(201, s);
        var projectId = Id(p);
        Assert.InRange((await _host.AddMemberAsync(_ba, projectId, _reviewer)).Status, 200, 201);
        var (ss, src) = await _ba.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve." });
        Assert.InRange(ss, 200, 201);
        return (projectId, Id(src));
    }

    [Fact]
    public async Task Approving_an_answered_clarification_question_marks_it_resolved()
    {
        var (_, sourceId) = await ProjectWithSourceAsync();
        var (an, analysis) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze");
        Assert.Equal(200, an);
        var qid = Id(analysis!["clarificationQuestions"]![0]);

        Assert.Equal(200, (await _ba.PostAsync($"/api/artifacts/{qid}/clarification-answer",
            new { answer = "Delegation is allowed.", notApplicable = false })).Status);
        var answered = (await _ba.GetAsync($"/api/artifacts/{qid}")).Body!;
        Assert.Equal("Answered", answered["data"]!["clarificationStatus"]!.GetValue<string>());
        Assert.Equal(2, answered["currentVersion"]!.GetValue<int>());

        var (approveStatus, approved) = await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{qid}/status", new { status = 3 });
        Assert.Equal(200, approveStatus);
        Assert.Equal("Resolved", approved!["data"]!["clarificationStatus"]!.GetValue<string>());
        // resolving is itself a DataJson change, so it gets its own version, same as answering did
        Assert.Equal(3, approved["currentVersion"]!.GetValue<int>());

        var versions = (await _ba.GetAsync($"/api/artifacts/{qid}/versions")).Body!.AsArray();
        Assert.Equal(3, versions.Count);
        Assert.Equal(2, versions.Count(v => v!["origin"]!.GetValue<int>() == 1)); // answer + resolution are both human-origin versions
    }

    [Fact]
    public async Task Approving_a_not_applicable_or_still_open_question_does_not_become_resolved()
    {
        var (_, sourceId) = await ProjectWithSourceAsync();
        var (an, analysis) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze");
        Assert.Equal(200, an);
        var questions = analysis!["clarificationQuestions"]!.AsArray();

        // Mark the first question Not Applicable, then approve the artifact - it must stay
        // NotApplicable, not flip to Resolved, and approving must not add another version.
        var naId = Id(questions[0]);
        Assert.Equal(200, (await _ba.PostAsync($"/api/artifacts/{naId}/clarification-answer",
            new { answer = "", notApplicable = true })).Status);
        var (naApproveStatus, naApproved) = await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{naId}/status", new { status = 3 });
        Assert.Equal(200, naApproveStatus);
        Assert.Equal("NotApplicable", naApproved!["data"]!["clarificationStatus"]!.GetValue<string>());
        Assert.Equal(2, naApproved["currentVersion"]!.GetValue<int>()); // unchanged by the approval

        // Approve a still-Open (never answered) question - it must stay Open, not Resolved.
        var openId = Id(questions[1]);
        var (openApproveStatus, openApproved) = await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{openId}/status", new { status = 3 });
        Assert.Equal(200, openApproveStatus);
        Assert.Equal("Open", openApproved!["data"]!["clarificationStatus"]!.GetValue<string>());
        Assert.Equal(1, openApproved["currentVersion"]!.GetValue<int>());
    }
}
