using System.Text.Json.Nodes;
using AiReap.Domain.Common;
using AiReap.Domain.Enums;
using Xunit;

namespace AiReap.Tests;

// §22/§24 — human-approval integrity. An approved artifact can never be changed while keeping its
// approval: an edit becomes a new version in UnderReview, the approved version stays intact and
// auditable, and status only moves along the ArtifactStatusTransitions state machine.
public class ArtifactApprovalIntegrityTests : IAsyncLifetime
{
    private const int AiGenerated = 0, Draft = 1, UnderReview = 2, Approved = 3, Rejected = 4, Implemented = 5, Verified = 6;

    private readonly TestHost _host = new();
    private ApiUser _ba = null!, _reviewer = null!, _dev = null!, _qa = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.RegisterAsync("BusinessAnalyst");
        _reviewer = await _host.RegisterAsync("Reviewer");
        _dev = await _host.RegisterAsync("Developer");
        _qa = await _host.RegisterAsync("QA");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();
    private static int Int(JsonNode? n, string key) => n![key]!.GetValue<int>();

    private async Task<string> NewFunctionalRequirementAsync()
    {
        var (ps, project) = await _ba.PostAsync("/api/projects", new { name = "Integrity", description = "d" });
        Assert.Equal(201, ps);
        foreach (var member in new[] { _reviewer, _dev, _qa })
        {
            Assert.InRange((await _host.AddMemberAsync(_ba, Id(project), member)).Status, 200, 201);
        }

        var (_, source) = await _ba.PostAsync($"/api/projects/{Id(project)}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve them." });
        var (gs, generated) = await _ba.PostAsync($"/api/requirement-sources/{Id(source)}/generate-requirements");
        Assert.Equal(200, gs);
        return Id(generated!["functionalRequirements"]![0]);
    }

    private Task<(int Status, JsonNode? Body)> SetStatusAsync(ApiUser user, string id, int status, string? comment = null) =>
        user.SendAsync(HttpMethod.Patch, $"/api/artifacts/{id}/status", new { status, comment });

    private async Task<string> ApprovedFunctionalRequirementAsync()
    {
        var frId = await NewFunctionalRequirementAsync();
        Assert.Equal(200, (await SetStatusAsync(_reviewer, frId, Approved, "looks right")).Status);
        return frId;
    }

    [Fact]
    public async Task Editing_an_approved_artifact_creates_a_new_under_review_version_and_preserves_the_approved_one()
    {
        var frId = await ApprovedFunctionalRequirementAsync();
        var before = (await _ba.GetAsync($"/api/artifacts/{frId}")).Body!;
        Assert.Equal(Approved, Int(before, "status"));
        Assert.Equal(1, Int(before, "approvedVersion"));
        var approvedTitle = before["title"]!.GetValue<string>();
        var approvedData = before["data"]!.ToJsonString();

        var (status, edited) = await _ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}",
            new { title = "Submit leave request with attachment", priority = 3, reason = "Stakeholder asked for attachments" });

        Assert.Equal(200, status);
        // Not silently modified while still approved:
        Assert.Equal(UnderReview, Int(edited, "status"));
        Assert.Equal(2, Int(edited, "currentVersion"));
        Assert.Equal(1, Int(edited, "approvedVersion")); // still points at the approved content

        var versions = (await _ba.GetAsync($"/api/artifacts/{frId}/versions")).Body!.AsArray();
        Assert.Equal(2, versions.Count);

        // The previously approved version is unchanged and identifiable.
        var v1 = versions.Single(v => Int(v, "versionNumber") == 1)!;
        Assert.Equal(approvedTitle, v1["title"]!.GetValue<string>());
        Assert.Equal(approvedData, v1["data"]!.ToJsonString());

        // The new version carries the edit, who made it, why, and that it awaits review.
        var v2 = versions.Single(v => Int(v, "versionNumber") == 2)!;
        Assert.Equal("Submit leave request with attachment", v2["title"]!.GetValue<string>());
        Assert.Equal(3, Int(v2, "priority"));
        Assert.Equal(UnderReview, Int(v2, "status"));
        Assert.Equal(1, Int(v2, "origin")); // Human
        Assert.Contains("Stakeholder asked for attachments", v2["reason"]!.GetValue<string>());
        Assert.Contains("re-review required", v2["reason"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(v2["changedByUserId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_new_version_must_be_approved_again_and_approval_history_names_each_version()
    {
        var frId = await ApprovedFunctionalRequirementAsync();
        await _ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}", new { title = "Changed after approval" });

        var (s, reapproved) = await SetStatusAsync(_reviewer, frId, Approved, "re-reviewed");
        Assert.Equal(200, s);
        Assert.Equal(Approved, Int(reapproved, "status"));
        Assert.Equal(2, Int(reapproved, "approvedVersion"));

        var reviews = (await _ba.GetAsync($"/api/artifacts/{frId}/reviews")).Body!.AsArray();
        Assert.Equal(2, reviews.Count);
        Assert.Equal(new[] { 1, 2 }, reviews.Select(r => Int(r, "versionNumber")).Order().ToArray());
        Assert.All(reviews, r => Assert.Equal(0, Int(r, "decision"))); // ReviewDecision.Approved
        Assert.Contains(reviews, r => r!["comment"]!.GetValue<string>() == "looks right");
        Assert.Contains(reviews, r => r!["comment"]!.GetValue<string>() == "re-reviewed");
    }

    [Fact]
    public async Task A_no_op_edit_does_not_create_a_version_or_drop_the_approval()
    {
        var frId = await ApprovedFunctionalRequirementAsync();
        var current = (await _ba.GetAsync($"/api/artifacts/{frId}")).Body!;

        var (s, after) = await _ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}",
            new { title = current["title"]!.GetValue<string>() });

        Assert.Equal(200, s);
        Assert.Equal(Approved, Int(after, "status"));
        Assert.Equal(1, Int(after, "currentVersion"));
    }

    [Fact]
    public async Task Answering_an_approved_clarification_question_also_requires_re_review()
    {
        var (_, project) = await _ba.PostAsync("/api/projects", new { name = "Clarify", description = "d" });
        await _host.AddMemberAsync(_ba, Id(project), _reviewer);
        var (_, source) = await _ba.PostAsync($"/api/projects/{Id(project)}/requirement-sources", new { sourceType = 1, rawText = "Leave requests." });
        var (_, analysis) = await _ba.PostAsync($"/api/requirement-sources/{Id(source)}/analyze");
        var questionId = Id(analysis!["clarificationQuestions"]![0]);
        await _ba.PostAsync($"/api/artifacts/{questionId}/clarification-answer", new { answer = "Two levels", notApplicable = false });
        Assert.Equal(200, (await SetStatusAsync(_reviewer, questionId, Approved)).Status);

        var (s, changed) = await _ba.PostAsync($"/api/artifacts/{questionId}/clarification-answer", new { answer = "Actually three levels", notApplicable = false });

        Assert.Equal(200, s);
        Assert.Equal(UnderReview, Int(changed, "status"));
    }

    [Fact]
    public async Task Empty_title_is_rejected_with_400()
    {
        var frId = await NewFunctionalRequirementAsync();
        var (s, body) = await _ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}", new { title = "   " });
        Assert.Equal(400, s);
        Assert.Equal("validation_failed", body!["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(Implemented)] // system-derived only
    [InlineData(Verified)]    // system-derived only
    [InlineData(AiGenerated)] // can never go back to "AI generated"
    [InlineData(Approved)]    // already approved - nothing to decide
    public async Task Invalid_transitions_from_approved_are_rejected_with_409_and_leave_the_artifact_unchanged(int target)
    {
        var frId = await ApprovedFunctionalRequirementAsync();

        var (s, body) = await SetStatusAsync(_reviewer, frId, target);

        Assert.Equal(409, s);
        Assert.Equal("invalid_status_transition", body!["code"]!.GetValue<string>());
        Assert.Equal("Approved", body["from"]!.GetValue<string>());
        Assert.Equal(Approved, Int((await _ba.GetAsync($"/api/artifacts/{frId}")).Body, "status"));
        Assert.Single((await _ba.GetAsync($"/api/artifacts/{frId}/reviews")).Body!.AsArray()); // no bogus review recorded
    }

    [Fact]
    public async Task Status_cannot_jump_to_implemented_or_verified_from_ai_generated()
    {
        var frId = await NewFunctionalRequirementAsync();
        Assert.Equal(409, (await SetStatusAsync(_reviewer, frId, Verified)).Status);
        Assert.Equal(409, (await SetStatusAsync(_reviewer, frId, Implemented)).Status);
        Assert.Equal(AiGenerated, Int((await _ba.GetAsync($"/api/artifacts/{frId}")).Body, "status"));
    }

    [Fact]
    public async Task Draft_must_be_submitted_for_review_before_approval()
    {
        var frId = await NewFunctionalRequirementAsync();
        Assert.Equal(200, (await SetStatusAsync(_ba, frId, Draft)).Status);
        Assert.Equal(409, (await SetStatusAsync(_reviewer, frId, Approved)).Status);
        Assert.Equal(200, (await SetStatusAsync(_ba, frId, UnderReview)).Status);
        Assert.Equal(200, (await SetStatusAsync(_reviewer, frId, Approved)).Status);
    }

    [Fact]
    public async Task Approval_can_be_reopened_or_revoked_and_rejected_work_can_be_resubmitted()
    {
        var frId = await ApprovedFunctionalRequirementAsync();
        Assert.Equal(200, (await SetStatusAsync(_reviewer, frId, UnderReview)).Status);
        Assert.Equal(200, (await SetStatusAsync(_reviewer, frId, Rejected, "needs rework")).Status);
        Assert.Equal(200, (await SetStatusAsync(_ba, frId, Draft)).Status);
        Assert.Equal(200, (await SetStatusAsync(_ba, frId, UnderReview)).Status);
        Assert.Equal(200, (await SetStatusAsync(_reviewer, frId, Approved)).Status);
    }

    [Fact]
    public async Task Only_reviewer_capable_roles_can_approve_and_only_writers_can_edit()
    {
        var frId = await NewFunctionalRequirementAsync();

        Assert.Equal(403, (await SetStatusAsync(_dev, frId, Approved)).Status);
        Assert.Equal(403, (await SetStatusAsync(_qa, frId, Approved)).Status);
        Assert.Equal(403, (await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}", new { title = "reviewer edit" })).Status);
        Assert.Equal(403, (await _dev.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}", new { title = "dev edit" })).Status);
        Assert.Equal(AiGenerated, Int((await _ba.GetAsync($"/api/artifacts/{frId}")).Body, "status"));

        Assert.Equal(200, (await SetStatusAsync(_reviewer, frId, Approved)).Status);
    }

    [Fact]
    public async Task QA_can_generate_and_edit_test_cases_but_no_other_artifacts()
    {
        var frId = await NewFunctionalRequirementAsync();

        var (gs, cases) = await _qa.PostAsync($"/api/artifacts/{frId}/generate-test-cases");
        Assert.Equal(200, gs);
        var caseId = Id(cases![0]);

        var (es, edited) = await _qa.SendAsync(HttpMethod.Patch, $"/api/artifacts/{caseId}", new { title = "QA refined test" });
        Assert.Equal(200, es);
        Assert.Equal("QA refined test", edited!["title"]!.GetValue<string>());

        Assert.Equal(403, (await _qa.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}", new { title = "QA edit of a requirement" })).Status);
        Assert.Equal(403, (await SetStatusAsync(_qa, caseId, Approved)).Status); // approval stays with reviewers
    }

    [Fact]
    public async Task A_non_member_cannot_approve_even_with_a_reviewer_role()
    {
        var frId = await NewFunctionalRequirementAsync();
        var outsider = await _host.RegisterAsync("Reviewer"); // right role, not a project member

        Assert.Equal(403, (await SetStatusAsync(outsider, frId, Approved)).Status);
        Assert.Empty((await _ba.GetAsync($"/api/artifacts/{frId}/reviews")).Body!.AsArray());
    }

    [Fact]
    public async Task Approving_linked_tasks_and_tests_still_promotes_through_the_state_machine()
    {
        var frId = await ApprovedFunctionalRequirementAsync();
        var project = (await _ba.GetAsync($"/api/artifacts/{frId}")).Body!;
        var sourceId = project["requirementSourceId"]!.GetValue<string>();

        var (_, tasks) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-tasks");
        foreach (var task in tasks!.AsArray()) await SetStatusAsync(_reviewer, Id(task), Approved);
        Assert.Equal(Implemented, Int((await _ba.GetAsync($"/api/artifacts/{frId}")).Body, "status"));

        var (_, cases) = await _ba.PostAsync($"/api/artifacts/{frId}/generate-test-cases");
        foreach (var testCase in cases!.AsArray()) await SetStatusAsync(_reviewer, Id(testCase), Approved);
        Assert.Equal(Verified, Int((await _ba.GetAsync($"/api/artifacts/{frId}")).Body, "status"));

        // Editing a verified requirement drops it back to review, like any approved content.
        var (_, edited) = await _ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}", new { title = "Changed after verification" });
        Assert.Equal(UnderReview, Int(edited, "status"));
    }
}

// The state machine itself, without HTTP.
public class ArtifactStatusTransitionsTests
{
    [Theory]
    [InlineData(ArtifactStatus.AiGenerated, ArtifactStatus.Approved, true)]
    [InlineData(ArtifactStatus.AiGenerated, ArtifactStatus.UnderReview, true)]
    [InlineData(ArtifactStatus.Draft, ArtifactStatus.UnderReview, true)]
    [InlineData(ArtifactStatus.Draft, ArtifactStatus.Approved, false)]
    [InlineData(ArtifactStatus.UnderReview, ArtifactStatus.Approved, true)]
    [InlineData(ArtifactStatus.UnderReview, ArtifactStatus.Rejected, true)]
    [InlineData(ArtifactStatus.Rejected, ArtifactStatus.Approved, false)]
    [InlineData(ArtifactStatus.Approved, ArtifactStatus.Approved, false)]
    [InlineData(ArtifactStatus.Approved, ArtifactStatus.Implemented, false)]
    [InlineData(ArtifactStatus.Approved, ArtifactStatus.Draft, false)]
    [InlineData(ArtifactStatus.Implemented, ArtifactStatus.Verified, false)]
    [InlineData(ArtifactStatus.Verified, ArtifactStatus.UnderReview, true)]
    [InlineData(ArtifactStatus.Verified, ArtifactStatus.Approved, false)]
    public void Human_transitions(ArtifactStatus from, ArtifactStatus to, bool allowed)
    {
        Assert.Equal(allowed, ArtifactStatusTransitions.CanHumanTransition(from, to));
        if (!allowed)
        {
            Assert.Throws<InvalidArtifactStatusTransitionException>(() => ArtifactStatusTransitions.EnsureHumanTransition(from, to));
        }
    }

    [Fact]
    public void Only_approved_to_implemented_to_verified_is_system_promotion()
    {
        Assert.True(ArtifactStatusTransitions.CanSystemPromote(ArtifactStatus.Approved, ArtifactStatus.Implemented));
        Assert.True(ArtifactStatusTransitions.CanSystemPromote(ArtifactStatus.Implemented, ArtifactStatus.Verified));
        Assert.False(ArtifactStatusTransitions.CanSystemPromote(ArtifactStatus.UnderReview, ArtifactStatus.Implemented));
        Assert.False(ArtifactStatusTransitions.CanSystemPromote(ArtifactStatus.Approved, ArtifactStatus.Verified));
    }
}
