using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// §4 - "Developer: view approved requirements, designs, stories and implementation tasks." Enforced
// in the backend (ArtifactVisibility): a Developer sees a requirement-side artifact (requirement, NFR,
// rule, story, acceptance criterion, ...) only once it is Approved or later (Implemented, Verified).
// §17 - generated designs need the Developer's technical review before approval, so technical
// artifacts (design, API specification, data entity, implementation task) are visible to a Developer
// in every status. The rule holds on every route that returns artifact data - list, by id, versions,
// reviews, relationships, work records, impact, notices, traceability, audit trail, copilot,
// dashboard, agent runs and lifecycle exit checks. A hidden artifact answers exactly like a missing
// one. Every other role keeps full visibility.
public class DeveloperArtifactVisibilityTests : IAsyncLifetime
{
    private const int AiGenerated = 0, Draft = 1, UnderReview = 2, Approved = 3, Rejected = 4, Implemented = 5;
    private const int FunctionalRequirement = 0, NonFunctionalRequirement = 1, BusinessRule = 2, UserStory = 3,
        DesignArtifact = 6, ApiSpecification = 7, DataEntity = 8, ImplementationTask = 9;
    private const int AnalysisStage = 2, DesignStage = 3, ApprovedStage = 4; // ProjectStatus
    private const int ReviewApproved = 0, ReviewRejected = 1, ReviewCommentOnly = 2; // ReviewDecision
    private static readonly int[] TechnicalTypes = [DesignArtifact, ApiSpecification, DataEntity, ImplementationTask];

    private readonly TestHost _host = new();
    private ApiUser _ba = null!, _reviewer = null!, _dev = null!, _qa = null!, _admin = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.RegisterAsync("BusinessAnalyst");
        _reviewer = await _host.RegisterAsync("Reviewer");
        _dev = await _host.RegisterAsync("Developer");
        _qa = await _host.RegisterAsync("QA");
        _admin = await _host.RegisterAsync("Administrator");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();

    // Requirement side: FR Approved (visible to a Developer); story Draft, NFR UnderReview and rule
    // AiGenerated (hidden). Technical side, all visible: task Implemented, design Rejected, data entity
    // AiGenerated, the other generated tasks AiGenerated.
    private sealed record Fixture(
        string ProjectId, string SourceId, string Fr, string Task, string Story, string Nfr, string Design, string Rule, string Entity,
        IReadOnlyDictionary<string, string> Codes, IReadOnlySet<string> Visible, int TotalCount)
    {
        public IEnumerable<string> Hidden => [Story, Nfr, Rule];
        public IEnumerable<string> UnapprovedTechnical => [Design, Entity];

        // Every artifact the Developer may not see: the three above plus any other unapproved
        // requirement-side artifact the stub AI generated.
        public IEnumerable<string> AllHidden => Codes.Keys.Where(id => !Visible.Contains(id));
    }

    private async Task<string> ProjectWithMembersAsync()
    {
        var (ps, project) = await _ba.PostAsync("/api/projects", new { name = "Visibility", description = "d" });
        Assert.Equal(201, ps);
        var projectId = Id(project);
        foreach (var member in new[] { _reviewer, _dev, _qa })
        {
            Assert.InRange((await _host.AddMemberAsync(_ba, projectId, member)).Status, 200, 201);
        }

        return projectId;
    }

    private async Task<string> SourceAsync(string projectId)
    {
        var (ss, source) = await _ba.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve them." });
        Assert.InRange(ss, 200, 201);
        return Id(source);
    }

    private async Task<Fixture> BuildAsync()
    {
        var projectId = await ProjectWithMembersAsync();
        var sourceId = await SourceAsync(projectId);
        foreach (var step in new[] { "generate-requirements", "generate-user-stories", "generate-business-rules", "generate-design", "generate-data-entities", "generate-tasks" })
        {
            Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/{step}")).Status);
        }

        var all = (await _ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray();
        string Of(int type) => Id(all.First(a => a!["artifactType"]!.GetValue<int>() == type));
        var fr = Of(FunctionalRequirement);
        var task = Of(ImplementationTask);
        var story = Of(UserStory);
        var nfr = Of(NonFunctionalRequirement);
        var design = Of(DesignArtifact);

        Assert.Equal(200, (await SetStatusAsync(_reviewer, fr, Approved)).Status);
        Assert.Equal(200, (await SetStatusAsync(_reviewer, task, Approved)).Status);
        await _host.ExecuteAsync($"UPDATE Artifacts SET Status = {Implemented} WHERE Id = '{task}'");
        Assert.Equal(200, (await SetStatusAsync(_ba, story, Draft)).Status);
        Assert.Equal(200, (await SetStatusAsync(_ba, nfr, UnderReview)).Status);
        Assert.Equal(200, (await SetStatusAsync(_reviewer, design, Rejected)).Status);

        // A review row on a hidden artifact, so the reviews endpoint has something it must not reveal.
        Assert.Equal(201, (await _reviewer.PostAsync($"/api/artifacts/{story}/reviews", new { comment = "Needs a persona." })).Status);

        var codes = all.ToDictionary(a => Id(a), a => a!["code"]!.GetValue<string>());
        var visible = all
            .Where(a => Id(a) == fr || TechnicalTypes.Contains(a!["artifactType"]!.GetValue<int>()))
            .Select(Id).ToHashSet();
        return new Fixture(projectId, sourceId, fr, task, story, nfr, design, Of(BusinessRule), Of(DataEntity), codes, visible, all.Count);
    }

    private static Task<(int Status, JsonNode? Body)> SetStatusAsync(ApiUser user, string id, int status, string comment = "test") =>
        user.SendAsync(HttpMethod.Patch, $"/api/artifacts/{id}/status", new { status, comment });

    private static IEnumerable<string> Ids(JsonNode? list) => list!.AsArray().Select(Id);

    private static void AssertNamesNone(string text, Fixture f)
    {
        foreach (var hidden in f.AllHidden)
        {
            Assert.DoesNotContain(f.Codes[hidden], text);
        }
    }

    // ---- lists and by-id -------------------------------------------------------------------

    [Fact]
    public async Task Developer_lists_approved_requirements_and_every_technical_artifact_whatever_the_filter()
    {
        var f = await BuildAsync();

        var (status, list) = await _dev.GetAsync($"/api/projects/{f.ProjectId}/artifacts");
        Assert.Equal(200, status);
        Assert.Equal(f.Visible.Order(), Ids(list).Order());
        Assert.Contains(f.Design, Ids(list));
        Assert.Contains(f.Entity, Ids(list));

        Assert.Empty((await _dev.GetAsync($"/api/projects/{f.ProjectId}/artifacts?type=UserStory")).Body!.AsArray());
        Assert.Empty((await _dev.GetAsync($"/api/projects/{f.ProjectId}/artifacts?type=BusinessRule")).Body!.AsArray());
        Assert.Equal([f.Fr], Ids((await _dev.GetAsync($"/api/projects/{f.ProjectId}/artifacts?type=FunctionalRequirement")).Body));
        Assert.Contains(f.Design, Ids((await _dev.GetAsync($"/api/projects/{f.ProjectId}/artifacts?type=DesignArtifact")).Body));
        Assert.Equal(f.Visible.Order(),
            Ids((await _dev.GetAsync($"/api/projects/{f.ProjectId}/artifacts?requirementSourceId={f.SourceId}")).Body).Order());
    }

    [Fact]
    public async Task Developer_can_get_approved_requirements_and_technical_artifacts_in_any_status_by_id()
    {
        var f = await BuildAsync();

        var (fs, fr) = await _dev.GetAsync($"/api/artifacts/{f.Fr}");
        Assert.Equal(200, fs);
        Assert.Equal(Approved, fr!["status"]!.GetValue<int>());

        var (ts, task) = await _dev.GetAsync($"/api/artifacts/{f.Task}");
        Assert.Equal(200, ts);
        Assert.Equal(Implemented, task!["status"]!.GetValue<int>());

        var (ds, design) = await _dev.GetAsync($"/api/artifacts/{f.Design}");
        Assert.Equal(200, ds);
        Assert.Equal(Rejected, design!["status"]!.GetValue<int>());

        var (es, entity) = await _dev.GetAsync($"/api/artifacts/{f.Entity}");
        Assert.Equal(200, es);
        Assert.Equal(AiGenerated, entity!["status"]!.GetValue<int>());
    }

    [Fact]
    public async Task Developer_cannot_get_unapproved_requirement_artifacts_by_id()
    {
        var f = await BuildAsync();

        Assert.Equal(404, (await _dev.GetAsync($"/api/artifacts/{f.Story}")).Status); // Draft
        Assert.Equal(404, (await _dev.GetAsync($"/api/artifacts/{f.Nfr}")).Status);   // UnderReview
        Assert.Equal(404, (await _dev.GetAsync($"/api/artifacts/{f.Rule}")).Status);  // AiGenerated
    }

    [Fact]
    public async Task Developer_cannot_reach_an_unapproved_requirement_through_its_sub_resources()
    {
        var f = await BuildAsync();

        foreach (var hidden in f.Hidden)
        {
            Assert.Empty((await _dev.GetAsync($"/api/artifacts/{hidden}/versions")).Body!.AsArray());
            Assert.Empty((await _dev.GetAsync($"/api/artifacts/{hidden}/reviews")).Body!.AsArray());
            Assert.Empty((await _dev.GetAsync($"/api/artifacts/{hidden}/relationships")).Body!.AsArray());
            Assert.Empty((await _dev.GetAsync($"/api/artifacts/{hidden}/impact-notices")).Body!.AsArray());
            Assert.Equal(404, (await _dev.GetAsync($"/api/artifacts/{hidden}/work-records")).Status);
            Assert.Equal(404, (await _dev.GetAsync($"/api/artifacts/{hidden}/impact")).Status);
        }

        // The same sub-resources answer for a visible artifact, approved or technical.
        Assert.NotEmpty((await _dev.GetAsync($"/api/artifacts/{f.Fr}/versions")).Body!.AsArray());
        Assert.NotEmpty((await _dev.GetAsync($"/api/artifacts/{f.Fr}/reviews")).Body!.AsArray());
        Assert.Equal(200, (await _dev.GetAsync($"/api/artifacts/{f.Task}/work-records")).Status);
        Assert.NotEmpty((await _dev.GetAsync($"/api/artifacts/{f.Entity}/versions")).Body!.AsArray());
        Assert.NotEmpty((await _dev.GetAsync($"/api/artifacts/{f.Design}/reviews")).Body!.AsArray()); // the rejection
    }

    [Fact]
    public async Task Developer_sees_only_visible_related_artifacts_in_relationships_and_impact()
    {
        var f = await BuildAsync();

        var baRelated = (await _ba.GetAsync($"/api/artifacts/{f.Fr}/relationships")).Body!.AsArray()
            .Select(r => r!["relatedArtifactId"]!.GetValue<string>()).ToList();
        Assert.Contains(f.Story, baRelated);
        var related = (await _dev.GetAsync($"/api/artifacts/{f.Fr}/relationships")).Body!.AsArray()
            .Select(r => r!["relatedArtifactId"]!.GetValue<string>()).ToList();
        Assert.Equal(baRelated.Where(f.Visible.Contains).Order(), related.Order());
        Assert.Contains(f.Task, related);
        Assert.Contains(f.Entity, related);

        var (imp, impact) = await _dev.GetAsync($"/api/artifacts/{f.Fr}/impact");
        Assert.Equal(200, imp);
        Assert.Equal([f.Task], impact!["approvedOrLaterDownstream"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()));
        var other = impact["otherDownstream"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()).ToList();
        Assert.All(other, id => Assert.Contains(id, f.Visible));
        Assert.Contains(f.Entity, other);
    }

    // ---- aggregate views -------------------------------------------------------------------

    [Fact]
    public async Task Developer_traceability_audit_trail_and_copilot_exclude_unapproved_requirements()
    {
        var f = await BuildAsync();

        var row = Assert.Single((await _dev.GetAsync($"/api/projects/{f.ProjectId}/traceability-matrix")).Body!.AsArray())!;
        Assert.Equal(f.Fr, row["functionalRequirementId"]!.GetValue<string>());
        Assert.Contains(f.Task, row["implementationTasks"]!.AsArray().Select(Id));
        Assert.Empty(row["userStories"]!.AsArray());
        Assert.Empty(row["businessRules"]!.AsArray());
        Assert.NotEmpty(row["dataEntities"]!.AsArray());

        var audit = (await _dev.GetAsync($"/api/projects/{f.ProjectId}/ai-executions")).Body!.AsArray();
        var produced = audit.Select(e => e!["producedArtifactId"]?.GetValue<string>()).ToHashSet();
        Assert.All(produced, id => Assert.Contains(id!, f.Visible));
        Assert.Contains(f.Fr, produced);
        Assert.Contains(f.Entity, produced);
        var baAudit = (await _ba.GetAsync($"/api/projects/{f.ProjectId}/ai-executions")).Body!.AsArray();
        Assert.Contains(baAudit, e => e!["producedArtifactId"]?.GetValue<string>() == f.Story);

        Assert.Equal(200, (await _dev.PostAsync($"/api/projects/{f.ProjectId}/copilot/ask", new { question = "What is planned?" })).Status);
        var prompt = _host.Ai.UserPrompts[^1];
        Assert.Contains(f.Codes[f.Fr], prompt);
        Assert.Contains(f.Codes[f.Entity], prompt);
        AssertNamesNone(prompt, f);
    }

    [Fact]
    public async Task Developer_dashboard_recent_changes_omit_unapproved_requirements_but_counts_are_unchanged()
    {
        var f = await BuildAsync();

        var (bs, baDash) = await _ba.GetAsync($"/api/projects/{f.ProjectId}/dashboard");
        var (ds, devDash) = await _dev.GetAsync($"/api/projects/{f.ProjectId}/dashboard");
        Assert.Equal(200, bs);
        Assert.Equal(200, ds);

        var baRecent = baDash!["recentChanges"]!.AsArray().Select(c => c!["code"]!.GetValue<string>()).ToList();
        var devRecent = devDash!["recentChanges"]!.AsArray().Select(c => c!["code"]!.GetValue<string>()).ToList();
        Assert.Contains(f.Codes[f.Story], baRecent);
        Assert.NotEmpty(devRecent);
        var visibleCodes = f.Visible.Select(id => f.Codes[id]).ToHashSet();
        Assert.All(devRecent, code => Assert.Contains(code, visibleCodes));
        AssertNamesNone(devDash["recentChanges"]!.ToJsonString(), f);

        // Aggregate counts stay project-wide for every caller.
        foreach (var count in new[] { "functionalRequirementCount", "nonFunctionalRequirementCount", "businessRuleCount", "userStoryCount",
                     "approvedCount", "pendingReviewCount", "rejectedCount", "testedFunctionalRequirementCount", "requirementsNeedingReviewCount" })
        {
            Assert.Equal(baDash[count]!.GetValue<int>(), devDash[count]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task Developer_agent_run_output_omits_unapproved_requirements_but_keeps_design_steps_decidable()
    {
        var projectId = await ProjectWithMembersAsync();
        var sourceId = await SourceAsync(projectId);
        var (rs, started) = await _ba.PostAsync("/api/agent-runs", new { requirementSourceId = sourceId });
        Assert.Equal(200, rs);
        var runId = Id(started);

        // Requirements, then Analysis: decided by the BA; their outputs are requirement-side drafts.
        Assert.Equal(200, (await _ba.PostAsync($"/api/agent-runs/{runId}/decision", new { approve = true })).Status);
        Assert.Equal(200, (await _ba.PostAsync($"/api/agent-runs/{runId}/decision", new { approve = true })).Status);

        static JsonArray Produced(JsonNode run, int step) => run["stages"]![step]!["output"]!["producedArtifacts"]!.AsArray();
        var baRun = (await _ba.GetAsync($"/api/agent-runs/{runId}")).Body!;
        var (gs, devRun) = await _dev.GetAsync($"/api/agent-runs/{runId}");
        Assert.Equal(200, gs);

        var hiddenCodes = (await _ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray()
            .Where(a => !TechnicalTypes.Contains(a!["artifactType"]!.GetValue<int>()) && a["status"]!.GetValue<int>() != Approved)
            .Select(a => a!["code"]!.GetValue<string>()).ToList();
        Assert.NotEmpty(hiddenCodes);

        // The Analysis step produced requirements (unapproved): listed for the BA, not for the Developer.
        Assert.NotEmpty(Produced(baRun, 1));
        Assert.Empty(Produced(devRun!, 0));
        Assert.Empty(Produced(devRun!, 1));
        foreach (var step in devRun!["stages"]!.AsArray())
        {
            Assert.Equal(step!["status"]!.GetValue<int>(), baRun["stages"]![step["order"]!.GetValue<int>()]!["status"]!.GetValue<int>());
            if (step["output"] is { } output)
            {
                foreach (var code in hiddenCodes)
                {
                    Assert.DoesNotContain(code, output["producedArtifacts"]!.ToJsonString());
                    Assert.All(output["findings"]!.AsArray(), finding => Assert.DoesNotMatch($@"\b{code}\b", finding!.GetValue<string>()));
                }
            }
        }

        // The Architecture step's designs are technical: the Developer sees them and decides the step.
        Assert.NotEmpty(Produced(devRun, 2));
        Assert.Equal(Ids(Produced(baRun, 2)).Order(), Ids(Produced(devRun, 2)).Order());
        var (decided, afterDecision) = await _dev.PostAsync($"/api/agent-runs/{runId}/decision", new { approve = true });
        Assert.Equal(200, decided);
        Assert.Empty(Produced(afterDecision!, 1));
        Assert.NotEmpty(Produced(afterDecision!, 3)); // Development Planning's tasks, now awaiting the Developer

        // The Developer's project list of runs is filtered the same way.
        var listed = (await _dev.GetAsync($"/api/projects/{projectId}/agent-runs")).Body!.AsArray().Single()!;
        Assert.Empty(Produced(listed, 1));
    }

    [Fact]
    public async Task Developer_lifecycle_exit_checks_count_but_do_not_name_unapproved_requirements()
    {
        var f = await BuildAsync();

        foreach (var stage in new[] { AnalysisStage, DesignStage })
        {
            await _host.ExecuteAsync($"UPDATE Projects SET Status = {stage} WHERE Id = '{f.ProjectId}'");
            var (bs, baState) = await _ba.GetAsync($"/api/projects/{f.ProjectId}/lifecycle");
            var (ds, devState) = await _dev.GetAsync($"/api/projects/{f.ProjectId}/lifecycle");
            Assert.Equal(200, bs);
            Assert.Equal(200, ds);

            // Same checks, same outcomes: only the named codes differ.
            var baChecks = baState!["exitChecks"]!.AsArray();
            var devChecks = devState!["exitChecks"]!.AsArray();
            Assert.Equal(baChecks.Select(c => (c!["label"]!.GetValue<string>(), c["passed"]!.GetValue<bool>())),
                devChecks.Select(c => (c!["label"]!.GetValue<string>(), c["passed"]!.GetValue<bool>())));
            AssertNamesNone(devState.ToJsonString(), f);
        }

        // In Design, the BA is told which requirement left the baseline; the Developer only how many.
        var baBaseline = (await _ba.GetAsync($"/api/projects/{f.ProjectId}/lifecycle")).Body!["exitChecks"]!.AsArray()
            .Single(c => c!["label"]!.GetValue<string>() == "Requirements baseline still approved")!["detail"]!.GetValue<string>();
        var devBaseline = (await _dev.GetAsync($"/api/projects/{f.ProjectId}/lifecycle")).Body!["exitChecks"]!.AsArray()
            .Single(c => c!["label"]!.GetValue<string>() == "Requirements baseline still approved")!["detail"]!.GetValue<string>();
        Assert.Contains(f.Codes[f.Story], baBaseline);
        Assert.DoesNotContain(f.Codes[f.Story], devBaseline);
        Assert.Matches(@"^\d+ requirement artifact\(s\) are not approved \(not listed until approved\)\.$", devBaseline);

        // A failed submission's evidence error names nothing hidden either.
        var (ss, problem) = await _dev.PostAsync($"/api/projects/{f.ProjectId}/lifecycle/transitions", new { targetStatus = ApprovedStage, note = "x" });
        Assert.Equal(409, ss);
        AssertNamesNone(problem!.ToJsonString(), f);
    }

    // ---- reviews ---------------------------------------------------------------------------

    [Fact]
    public async Task Developer_reviews_technical_design_before_approval_but_not_unapproved_requirements()
    {
        var f = await BuildAsync();

        var (s, review) = await _dev.PostAsync($"/api/artifacts/{f.Entity}/reviews", new { comment = "Index EmployeeId." });
        Assert.Equal(201, s);
        Assert.Equal(ReviewCommentOnly, review!["decision"]!.GetValue<int>());
        Assert.Equal("Developer", review["reviewerRole"]!.GetValue<string>());
        Assert.Equal(AiGenerated, (await _ba.GetAsync($"/api/artifacts/{f.Entity}")).Body!["status"]!.GetValue<int>()); // unchanged

        Assert.Equal(404, (await _dev.PostAsync($"/api/artifacts/{f.Story}/reviews", new { comment = "x" })).Status);
        Assert.Equal(404, (await _dev.PostAsync($"/api/artifacts/{f.Rule}/reviews", new { comment = "x" })).Status);

        // Reviewing is not approving: a Developer still cannot change an artifact's status or content.
        Assert.Equal(403, (await SetStatusAsync(_dev, f.Entity, Approved)).Status);
        Assert.Equal(403, (await _dev.SendAsync(HttpMethod.Patch, $"/api/artifacts/{f.Entity}", new { title = "x" })).Status);
    }

    [Fact]
    public async Task Design_flow_developer_reviews_reviewer_approves_developer_submits_and_cannot_approve_the_gate()
    {
        var projectId = await ProjectWithMembersAsync();
        var sourceId = await SourceAsync(projectId);
        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements")).Status);
        var (_, stories) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-user-stories");
        foreach (var story in stories!.AsArray())
        {
            await _ba.PostAsync($"/api/artifacts/{Id(story)}/generate-acceptance-criteria");
        }

        foreach (var step in new[] { "generate-design", "generate-data-entities", "generate-api-specs" })
        {
            Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/{step}")).Status);
        }

        // The requirements baseline was approved in Analysis; the designs are still AI drafts.
        await _host.ExecuteAsync($"UPDATE Artifacts SET Status = {Approved}, ApprovedVersion = CurrentVersion WHERE ProjectId = '{projectId}' AND ArtifactType IN (0,1,2,3,4)");
        await _host.ExecuteAsync($"UPDATE Projects SET Status = {DesignStage} WHERE Id = '{projectId}'");
        var designs = (await _ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray()
            .Where(a => a!["artifactType"]!.GetValue<int>() is DesignArtifact or ApiSpecification or DataEntity)
            .Select(Id).ToList();
        Assert.NotEmpty(designs);

        // 1. The Developer reviews each generated design before it is approved.
        foreach (var id in designs)
        {
            Assert.Equal(200, (await _dev.GetAsync($"/api/artifacts/{id}")).Status);
            Assert.Equal(201, (await _dev.PostAsync($"/api/artifacts/{id}/reviews", new { comment = "Technically sound." })).Status);
        }

        // 2. The Reviewer approves the design artifacts (the Developer's review still counts: same version).
        foreach (var id in designs)
        {
            Assert.Equal(200, (await SetStatusAsync(_reviewer, id, Approved, "Approved after developer review.")).Status);
        }

        var checks = (await _dev.GetAsync($"/api/projects/{projectId}/lifecycle")).Body!["exitChecks"]!.AsArray();
        Assert.All(checks, c => Assert.True(c!["passed"]!.GetValue<bool>(), c["label"] + ": " + c["detail"]));

        // 3. The Developer submits the Design gate; 4. they cannot approve it; the Reviewer does.
        var (ss, pending) = await _dev.PostAsync($"/api/projects/{projectId}/lifecycle/transitions", new { targetStatus = ApprovedStage, note = "Design reviewed." });
        Assert.Equal(201, ss);
        Assert.Equal(403, (await _dev.PostAsync($"/api/stage-transitions/{Id(pending)}/decision", new { approve = true })).Status);
        Assert.Equal(DesignStage, await _host.ScalarAsync($"SELECT Status FROM Projects WHERE Id = '{projectId}'"));
        Assert.Equal(200, (await _reviewer.PostAsync($"/api/stage-transitions/{Id(pending)}/decision", new { approve = true, reason = "Design baseline approved." })).Status);
        Assert.Equal(ApprovedStage, await _host.ScalarAsync($"SELECT Status FROM Projects WHERE Id = '{projectId}'"));
    }

    [Fact]
    public async Task Reviewer_comments_approves_and_rejects_artifacts_with_the_comment_recorded()
    {
        var f = await BuildAsync();

        // Comment only: recorded under the Reviewer role; the status is untouched.
        var (cs, comment) = await _reviewer.PostAsync($"/api/artifacts/{f.Rule}/reviews", new { comment = "Clarify the approval threshold." });
        Assert.Equal(201, cs);
        Assert.Equal(ReviewCommentOnly, comment!["decision"]!.GetValue<int>());
        Assert.Equal("Reviewer", comment["reviewerRole"]!.GetValue<string>());
        Assert.Equal(AiGenerated, (await _ba.GetAsync($"/api/artifacts/{f.Rule}")).Body!["status"]!.GetValue<int>());

        // Approve with a comment; reject with a reason.
        Assert.Equal(200, (await SetStatusAsync(_reviewer, f.Rule, Approved, "Threshold agreed.")).Status);
        Assert.Equal(200, (await SetStatusAsync(_reviewer, f.Nfr, Rejected, "Target is not measurable.")).Status);

        var ruleReviews = (await _ba.GetAsync($"/api/artifacts/{f.Rule}/reviews")).Body!.AsArray();
        Assert.Contains(ruleReviews, r => r!["decision"]!.GetValue<int>() == ReviewApproved && r["comment"]!.GetValue<string>() == "Threshold agreed."
                                          && r["reviewerRole"]!.GetValue<string>() == "Reviewer");
        Assert.Contains(ruleReviews, r => r!["decision"]!.GetValue<int>() == ReviewCommentOnly);
        var nfrReviews = (await _ba.GetAsync($"/api/artifacts/{f.Nfr}/reviews")).Body!.AsArray();
        Assert.Contains(nfrReviews, r => r!["decision"]!.GetValue<int>() == ReviewRejected && r["comment"]!.GetValue<string>() == "Target is not measurable.");
        Assert.Equal(Rejected, (await _ba.GetAsync($"/api/artifacts/{f.Nfr}")).Body!["status"]!.GetValue<int>());

        // Once approved the rule is visible to the Developer, with its review history.
        Assert.Equal(200, (await _dev.GetAsync($"/api/artifacts/{f.Rule}")).Status);
        Assert.NotEmpty((await _dev.GetAsync($"/api/artifacts/{f.Rule}/reviews")).Body!.AsArray());
    }

    [Fact]
    public async Task Only_reviewer_capable_roles_decide_artifacts()
    {
        var f = await BuildAsync();

        Assert.Equal(403, (await SetStatusAsync(_dev, f.Entity, Approved)).Status);
        Assert.Equal(403, (await SetStatusAsync(_qa, f.Entity, Approved)).Status);
        Assert.Equal(200, (await SetStatusAsync(_ba, f.Entity, Approved)).Status);
        Assert.Equal(200, (await SetStatusAsync(_admin, f.Rule, Rejected, "Duplicate rule.")).Status);
    }

    // ---- notices / other roles -------------------------------------------------------------

    [Fact]
    public async Task Developer_sees_impact_notices_only_on_artifacts_they_can_see()
    {
        var f = await BuildAsync();

        // Editing the approved FR puts it back UnderReview and flags everything downstream.
        Assert.Equal(200, (await _ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{f.Fr}", new { title = "Submit request with attachment" })).Status);
        var all = (await _ba.GetAsync($"/api/projects/{f.ProjectId}/impact-notices")).Body!.AsArray();
        var storyNotice = all.Single(n => n!["affectedArtifactId"]!.GetValue<string>() == f.Story);
        Assert.Contains(all, n => n!["affectedArtifactId"]!.GetValue<string>() == f.Task);

        var devNotices = (await _dev.GetAsync($"/api/projects/{f.ProjectId}/impact-notices")).Body!.AsArray();
        Assert.All(devNotices, n => Assert.Contains(n!["affectedArtifactId"]!.GetValue<string>(), f.Visible));
        Assert.DoesNotContain(devNotices, n => n!["affectedArtifactId"]!.GetValue<string>() == f.Story);
        var taskNotice = devNotices.Single(n => n!["affectedArtifactId"]!.GetValue<string>() == f.Task)!;
        // The changed source is named (code/title only) so the Developer knows what to re-check,
        // even though the source itself is now UnderReview and not readable by them.
        Assert.Equal(f.Codes[f.Fr], taskNotice["sourceArtifactCode"]!.GetValue<string>());
        Assert.Equal(404, (await _dev.GetAsync($"/api/artifacts/{f.Fr}")).Status);

        Assert.Equal(404, (await _dev.PostAsync($"/api/impact-notices/{Id(storyNotice)}/acknowledge", new { note = "x" })).Status);
        Assert.Equal(200, (await _dev.PostAsync($"/api/impact-notices/{Id(taskNotice)}/acknowledge", new { note = "Task still valid" })).Status);
    }

    [Fact]
    public async Task Business_analyst_qa_reviewer_and_administrator_keep_full_visibility()
    {
        var f = await BuildAsync();

        foreach (var user in new[] { _ba, _qa, _reviewer, _admin })
        {
            var list = (await user.GetAsync($"/api/projects/{f.ProjectId}/artifacts")).Body!.AsArray();
            Assert.Equal(f.TotalCount, list.Count);

            foreach (var hidden in f.Hidden.Concat(f.UnapprovedTechnical))
            {
                Assert.Equal(200, (await user.GetAsync($"/api/artifacts/{hidden}")).Status);
                Assert.NotEmpty((await user.GetAsync($"/api/artifacts/{hidden}/versions")).Body!.AsArray());
            }

            Assert.NotEmpty((await user.GetAsync($"/api/artifacts/{f.Story}/reviews")).Body!.AsArray());
            Assert.Equal(200, (await user.GetAsync($"/api/artifacts/{f.Story}/impact")).Status);

            var row = Assert.Single((await user.GetAsync($"/api/projects/{f.ProjectId}/traceability-matrix")).Body!.AsArray())!;
            Assert.NotEmpty(row["userStories"]!.AsArray());

            var recent = (await user.GetAsync($"/api/projects/{f.ProjectId}/dashboard")).Body!["recentChanges"]!.ToJsonString();
            Assert.Contains(f.Codes[f.Story], recent);
        }
    }
}
