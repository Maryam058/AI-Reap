using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// PDF §37 — "Required End-to-End Demonstration," run verbatim: the exact deliberately-incomplete
// complaint-management prompt, walked through every stage of the diagram in section 37, then an
// approved requirement is changed and Impact Analysis is checked against the actual downstream
// artifacts it names (not merely that the call succeeds or doesn't mutate anything — that weaker
// check already exists in RegressionTests; this asserts the reported ids are the real linked
// story/task/test-case).
//
// Content note: this environment has no Ai:Anthropic:ApiKey configured (same as every other test
// in this suite - see TestHost), so generated artifact *content* comes from the deterministic
// StubAiChatClient rather than a real model. The scenario's raw input text is still the exact §37
// prompt, and every pipeline stage, persistence step, relationship edge, and the impact-analysis
// graph walk are the real production code paths - only the LLM call itself is stubbed.
public class Section37DemoTests : IAsyncLifetime
{
    private const string RawComplaintRequirement =
        "We need a complaint management application where employees can submit complaints and managers investigate them.";

    private readonly TestHost _host = new();
    private ApiUser _ba = null!, _reviewer = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.RegisterAsync("BusinessAnalyst");
        _reviewer = await _host.RegisterAsync("Reviewer");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();

    [Fact]
    public async Task Complaint_management_scenario_runs_end_to_end_then_a_changed_requirement_shows_correct_impact()
    {
        // ---- Raw Requirement (verbatim §37 prompt) -------------------------------------------
        var (ps, project) = await _ba.PostAsync("/api/projects",
            new { name = "Complaint Management (PDF §37 demo)", description = "Required end-to-end demonstration scenario" });
        Assert.Equal(201, ps);
        var projectId = Id(project);

        // §38 DoD — project membership. _ba is already a member (the creator); _reviewer, who
        // approves the requirement below, has to be added explicitly.
        Assert.InRange((await _host.AddMemberAsync(_ba, projectId, _reviewer)).Status, 200, 201);

        var (srcStatus, source) = await _ba.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = RawComplaintRequirement });
        Assert.InRange(srcStatus, 200, 201);
        var sourceId = Id(source);

        // ---- Analysis -> Missing Information -> Clarification Questions ---------------------
        var (analyzeStatus, analysis) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze");
        Assert.Equal(200, analyzeStatus);
        var questions = analysis!["clarificationQuestions"]!.AsArray();
        // the whole point of §37's deliberately-incomplete input: gaps must be surfaced as
        // questions, never silently invented (§32) - the raw prompt names no approval hierarchy,
        // no notification rule, no investigation SLA.
        Assert.NotEmpty(questions);

        // ---- User Answers / Refines -> Updated Requirement -----------------------------------
        foreach (var q in questions)
        {
            Assert.Equal(200, (await _ba.PostAsync($"/api/artifacts/{Id(q)}/clarification-answer",
                new { answer = "Confirmed for this demo scenario.", notApplicable = false })).Status);
        }

        // ---- Functional + Non-Functional Requirements ----------------------------------------
        var (genStatus, generated) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        Assert.Equal(200, genStatus);
        var functionalRequirements = generated!["functionalRequirements"]!.AsArray();
        Assert.NotEmpty(functionalRequirements);
        Assert.NotEmpty(generated["nonFunctionalRequirements"]!.AsArray());
        var frId = Id(functionalRequirements[0]);

        // ---- Business Rules -------------------------------------------------------------------
        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-business-rules")).Status);

        // ---- User Stories -> Acceptance Criteria ----------------------------------------------
        var (storiesStatus, stories) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-user-stories");
        Assert.Equal(200, storiesStatus);
        Assert.NotEmpty(stories!.AsArray());
        var storyId = Id(stories[0]);
        var (acStatus, criteria) = await _ba.PostAsync($"/api/artifacts/{storyId}/generate-acceptance-criteria");
        Assert.Equal(200, acStatus);
        Assert.NotEmpty(criteria!.AsArray());

        // ---- Architecture / Database Proposal / API Proposal ---------------------------------
        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-design")).Status);
        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-data-entities")).Status);
        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-api-specs")).Status);

        // ---- Implementation Tasks -------------------------------------------------------------
        var (tasksStatus, tasks) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-tasks");
        Assert.Equal(200, tasksStatus);
        Assert.NotEmpty(tasks!.AsArray());
        var taskId = Id(tasks[0]);

        // ---- Test Cases -------------------------------------------------------------------------
        var (tcStatus, testCases) = await _ba.PostAsync($"/api/artifacts/{frId}/generate-test-cases");
        Assert.Equal(200, tcStatus);
        Assert.NotEmpty(testCases!.AsArray());
        var testCaseId = Id(testCases[0]);

        // ---- Traceability Matrix ----------------------------------------------------------------
        var (matrixStatus, matrix) = await _ba.GetAsync($"/api/projects/{projectId}/traceability-matrix");
        Assert.Equal(200, matrixStatus);
        var row = matrix!.AsArray().Single()!; // exactly one FR in this project, and it rolls up everything derived from it
        Assert.Equal(frId, row["functionalRequirementId"]!.GetValue<string>());
        Assert.NotEmpty(row["userStories"]!.AsArray());
        Assert.NotEmpty(row["implementationTasks"]!.AsArray());
        Assert.NotEmpty(row["testCases"]!.AsArray());

        // ==== §37 part 2: change an approved requirement, verify Impact Analysis =================

        // Approve the requirement first, so it's a real approved artifact before it changes.
        Assert.Equal(200, (await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}/status",
            new { status = 3, comment = "approved for §37 demo" })).Status);
        var approved = (await _ba.GetAsync($"/api/artifacts/{frId}")).Body!;
        Assert.Equal(3, approved["status"]!.GetValue<int>()); // Approved

        // Change it - a content edit on an already-approved requirement (§23: creates a new
        // version, never overwrites history).
        var changedTitle = approved["title"]!.GetValue<string>() + " - investigation SLA tightened to 24 hours";
        var (updateStatus, updatedArtifact) = await _ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}",
            new { title = changedTitle, reason = "Investigation SLA tightened after stakeholder review" });
        Assert.Equal(200, updateStatus);
        Assert.Equal(changedTitle, updatedArtifact!["title"]!.GetValue<string>());
        Assert.Equal(2, updatedArtifact["currentVersion"]!.GetValue<int>());

        var versions = (await _ba.GetAsync($"/api/artifacts/{frId}/versions")).Body!.AsArray();
        Assert.Equal(2, versions.Count);
        Assert.Equal(1, versions.Count(v => v!["origin"]!.GetValue<int>() == 1)); // exactly one human-origin version: the edit

        // Impact Analysis must name the actual linked downstream artifacts, not merely return
        // something non-empty (RegressionTests already covers "doesn't mutate anything").
        var (impactStatus, impact) = await _ba.GetAsync($"/api/artifacts/{frId}/impact");
        Assert.Equal(200, impactStatus);
        var allImpacted = impact!["approvedOrLaterDownstream"]!.AsArray()
            .Concat(impact["otherDownstream"]!.AsArray())
            .ToList();
        Assert.NotEmpty(allImpacted);
        var impactedIds = allImpacted.Select(Id).ToHashSet();
        Assert.Contains(storyId, impactedIds);   // the user story derived from this FR
        Assert.Contains(taskId, impactedIds);    // the implementation task that implements this FR
        Assert.Contains(testCaseId, impactedIds); // the test case that tests this FR

        // Advisory only - the downstream artifacts themselves must be untouched by the change.
        var storyAfter = (await _ba.GetAsync($"/api/artifacts/{storyId}")).Body!;
        Assert.Equal(1, storyAfter["currentVersion"]!.GetValue<int>());
    }
}
