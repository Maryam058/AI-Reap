using System.Text.Json.Nodes;
using AiReap.Domain.Common;
using Microsoft.Data.SqlClient;
using Xunit;
using static AiReap.Tests.LifecycleTestSupport;

namespace AiReap.Tests;

// GET /api/projects/{id}/lifecycle-review: a read-only, historical projection. The project is built
// through the real API (stub AI), then its lifecycle history is laid out on a fixed timeline so stage
// attribution can be asserted exactly:
//
//   08:00 Draft · 09:00 Requirements Gathering · 10:00 Analysis · 11:00 Design
//   12:00 Design -> Approved requested (Developer), 12:30 approved (Reviewer)
//   13:00 Implementation · 14:00 Testing · 15:00 rollback to Implementation · 16:00 Testing
//   17:00 Testing -> Completed requested (QA), 17:30 approved (Reviewer)
public class LifecycleReviewTests : IAsyncLifetime
{
    // ProjectStatus, ArtifactType, StageReviewState and TimelineEventKind as the API serializes them.
    private const int Draft = 0, Gathering = 1, Analysis = 2, Design = 3, Approved = 4, Implementation = 5, Testing = 6, Completed = 7;
    private const int FR = 0, NFR = 1, US = 3, AC = 4, Task = 9, TestCase = 10, BusinessObjective = 11;
    private const int StateCompleted = 0;
    private const int DerivedFrom = 0;

    private static readonly DateTime Day = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

    private readonly TestHost _host = new();
    private Actor _ba = null!, _dev = null!, _reviewer = null!, _qa = null!, _outsider = null!, _admin = null!;
    private string _projectId = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.ActorAsync(Roles.BusinessAnalyst);
        _dev = await _host.ActorAsync(Roles.Developer);
        _reviewer = await _host.ActorAsync(Roles.Reviewer);
        _qa = await _host.ActorAsync(Roles.QA);
        _outsider = await _host.ActorAsync(Roles.QA);
        _admin = await _host.AdminAsync();
        _projectId = await CompletedProjectAsync();
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string At(int hour, int minute = 0) => Day.AddHours(hour).AddMinutes(minute).ToString("yyyy-MM-dd HH:mm:ss");

    private Task SqlAsync(string sql) => _host.ExecuteAsync(sql);

    private async Task<List<string>> IdsAsync(string sql)
    {
        await using var conn = new SqlConnection(_host.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var ids = new List<string>();
        while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0).ToString());
        return ids;
    }

    private async Task<string> CompletedProjectAsync()
    {
        var (status, body) = await _ba.Api.PostAsync("/api/projects", new
        {
            name = "Meeting rooms",
            businessProblem = "Double bookings",
            objectives = "Stop double bookings",
            scope = "Office staff"
        });
        Assert.Equal(201, status);
        var id = Id(body);
        foreach (var member in new[] { _dev, _reviewer, _qa })
        {
            Assert.InRange((await _host.AddMemberAsync(_ba.Api, id, member.Api)).Status, 200, 201);
        }

        // A real lifecycle move, so the review reads rows the lifecycle service itself wrote.
        var (advanced, _) = await _ba.Api.PostAsync($"/api/projects/{id}/lifecycle/transitions", new { targetStatus = Gathering });
        Assert.Equal(201, advanced);

        var (_, source) = await _ba.Api.PostAsync($"/api/projects/{id}/requirement-sources",
            new { sourceType = 1, rawText = "Staff book meeting rooms; a room can't be double booked." });
        var sourceId = Id(source);
        Assert.Equal(200, (await _ba.Api.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements")).Status);
        var (_, stories) = await _ba.Api.PostAsync($"/api/requirement-sources/{sourceId}/generate-user-stories");
        foreach (var story in stories!.AsArray())
        {
            await _ba.Api.PostAsync($"/api/artifacts/{Id(story)}/generate-acceptance-criteria");
        }

        foreach (var step in new[] { "generate-design", "generate-data-entities", "generate-api-specs", "generate-tasks" })
        {
            Assert.Equal(200, (await _ba.Api.PostAsync($"/api/requirement-sources/{sourceId}/{step}")).Status);
        }

        // Approve everything except one functional requirement (left for the visibility test), and
        // record one real review through the API.
        await SqlAsync($"UPDATE Artifacts SET Status = 3, ApprovedVersion = CurrentVersion WHERE ProjectId = '{id}'");
        foreach (var fr in await IdsAsync($"SELECT Id FROM Artifacts WHERE ProjectId = '{id}' AND ArtifactType = {FR}"))
        {
            Assert.Equal(200, (await _ba.Api.PostAsync($"/api/artifacts/{fr}/generate-test-cases")).Status);
        }

        await SqlAsync($"UPDATE Artifacts SET Status = 3, ApprovedVersion = CurrentVersion WHERE ProjectId = '{id}'");
        var keepUnapproved = (await IdsAsync($"SELECT TOP 1 Id FROM Artifacts WHERE ProjectId = '{id}' AND ArtifactType = {FR} ORDER BY Code DESC")).Single();
        await SqlAsync($"UPDATE Artifacts SET Status = 0, ApprovedVersion = NULL WHERE Id = '{keepUnapproved}'");
        var firstFr = (await IdsAsync($"SELECT TOP 1 Id FROM Artifacts WHERE ProjectId = '{id}' AND ArtifactType = {FR} ORDER BY Code")).Single();
        Assert.Equal(201, (await _reviewer.Api.PostAsync($"/api/artifacts/{firstFr}/reviews", new { comment = "Looks right." })).Status);

        // ---- lay the history out on the fixed timeline ----
        string TypeTime(string types, string at) =>
            $"UPDATE Artifacts SET CreatedAt = '{at}', UpdatedAt = '{at}' WHERE ProjectId = '{id}' AND ArtifactType IN ({types});";
        await SqlAsync(
            $"UPDATE Projects SET CreatedAt = '{At(8)}', Status = {Completed} WHERE Id = '{id}';" +
            $"UPDATE ProjectStageTransitions SET RequestedAt = '{At(8)}', DecidedAt = '{At(8)}' WHERE ProjectId = '{id}' AND Sequence = 0;" +
            $"UPDATE ProjectStageTransitions SET RequestedAt = '{At(9)}', DecidedAt = '{At(9)}' WHERE ProjectId = '{id}' AND Sequence = 1;" +
            $"UPDATE RequirementSources SET CreatedAt = '{At(9, 10)}' WHERE ProjectId = '{id}';" +
            TypeTime("11", At(8, 5)) +
            TypeTime("0,1,2,3,5", At(9, 30)) +
            TypeTime("4", At(10, 30)) +
            TypeTime("6,7,8", At(11, 30)) +
            TypeTime("9", At(12, 45)) +
            TypeTime("10", At(14, 30)) +
            // One artifact from before lifecycle history begins: its stage can't be known.
            $"UPDATE Artifacts SET CreatedAt = '{At(7)}' WHERE Id = (SELECT TOP 1 Id FROM Artifacts WHERE ProjectId = '{id}' AND ArtifactType = {NFR} ORDER BY Code);" +
            $"UPDATE v SET ChangedAt = a.CreatedAt FROM ArtifactVersions v JOIN Artifacts a ON a.Id = v.ArtifactId WHERE a.ProjectId = '{id}';" +
            $"UPDATE e SET Timestamp = a.CreatedAt FROM AIExecutions e JOIN Artifacts a ON a.Id = e.ProducedArtifactId WHERE e.ProjectId = '{id}';" +
            $"UPDATE r SET ReviewedAt = '{At(10, 15)}' FROM ArtifactReviews r JOIN Artifacts a ON a.Id = r.ArtifactId WHERE a.ProjectId = '{id}';");

        string Move(int seq, int kind, int from, int to, Actor by, string at, Actor? decider = null, string? decidedAt = null, string? note = null) =>
            "INSERT INTO ProjectStageTransitions (Id, ProjectId, Sequence, Kind, State, FromStatus, ToStatus, RequestedByUserId, RequestedByRole, RequestedAt, " +
            "RequestNote, DecidedByUserId, DecidedByRole, DecidedAt, IsOverride, EvidenceSnapshotJson) VALUES " +
            $"(NEWID(), '{id}', {seq}, {kind}, 1, {from}, {to}, '{by.Id}', '{by.Role}', '{at}', {(note is null ? "NULL" : $"N'{note}'")}, " +
            $"'{(decider ?? by).Id}', '{(decider ?? by).Role}', '{decidedAt ?? at}', 0, N'[]');";
        await SqlAsync(
            Move(2, 1, Gathering, Analysis, _ba, At(10)) +
            Move(3, 1, Analysis, Design, _ba, At(11)) +
            Move(4, 1, Design, Approved, _dev, At(12), _reviewer, At(12, 30)) +
            Move(5, 1, Approved, Implementation, _reviewer, At(13)) +
            Move(6, 1, Implementation, Testing, _dev, At(14)) +
            Move(7, 2, Testing, Implementation, _admin, At(15), note: "Two tasks were not done.") +
            Move(8, 1, Implementation, Testing, _dev, At(16)) +
            Move(9, 1, Testing, Completed, _qa, At(17), _reviewer, At(17, 30)));

        // Implementation and verification evidence, in both Implementation visits and in Testing.
        string Work(string artifactWhere, int outcome, Actor by, string at) =>
            "INSERT INTO ArtifactWorkRecords (Id, ArtifactId, VersionNumber, Outcome, Note, RecordedByUserId, RecordedByRole, RecordedAt) " +
            $"SELECT NEWID(), Id, CurrentVersion, {outcome}, NULL, '{by.Id}', '{by.Role}', '{at}' FROM Artifacts WHERE ProjectId = '{id}' AND {artifactWhere};";
        await SqlAsync(
            Work($"ArtifactType = {Task}", 0, _dev, At(13, 30)) +
            Work($"ArtifactType = {TestCase}", 2, _qa, At(16, 30)) +
            Work($"Id = (SELECT TOP 1 Id FROM Artifacts WHERE ProjectId = '{id}' AND ArtifactType = {TestCase} ORDER BY Code)", 3, _qa, At(14, 45)) +
            Work($"Id = (SELECT TOP 1 Id FROM Artifacts WHERE ProjectId = '{id}' AND ArtifactType = {TestCase} ORDER BY Code)", 2, _qa, At(16, 40)));

        return id;
    }

    private async Task<JsonNode> ReviewAsync(Actor actor, string? projectId = null)
    {
        var (status, body) = await actor.Api.GetAsync($"/api/projects/{projectId ?? _projectId}/lifecycle-review");
        Assert.Equal(200, status);
        return body!;
    }

    private static JsonNode Stage(JsonNode review, int stage) => review["stages"]!.AsArray().Single(s => s!["stage"]!.GetValue<int>() == stage)!;

    private static List<JsonNode> Artifacts(JsonNode review) => review["artifacts"]!.AsArray().Select(a => a!).ToList();

    private static int Int(JsonNode? n) => n!.GetValue<int>();

    // ---- tests ---------------------------------------------------------------------------------

    [Fact]
    public async Task Completed_project_reports_every_stage_completed_in_lifecycle_order()
    {
        var review = await ReviewAsync(_ba);

        Assert.True(review["isCompleted"]!.GetValue<bool>());
        Assert.True(review["historyComplete"]!.GetValue<bool>());
        Assert.Equal(Day.AddHours(17).AddMinutes(30), review["completedAt"]!.GetValue<DateTime>().ToUniversalTime());
        var stages = review["stages"]!.AsArray();
        Assert.Equal([Draft, Gathering, Analysis, Design, Approved, Implementation, Testing, Completed], stages.Select(s => Int(s!["stage"])));
        Assert.All(stages, s => Assert.Equal(StateCompleted, Int(s!["state"])));
        Assert.All(stages, s => Assert.False(s!["historyUnavailable"]!.GetValue<bool>()));
        Assert.Equal("Developer", Stage(review, Design)["ownerRole"]!.GetValue<string>());
    }

    [Fact]
    public async Task Artifacts_are_grouped_by_the_stage_the_project_was_in_when_they_were_created()
    {
        var review = await ReviewAsync(_ba);
        var artifacts = Artifacts(review);
        HashSet<int> TypesIn(int stage) => artifacts.Where(a => a["createdInStage"]?.GetValue<int>() == stage).Select(a => Int(a["artifactType"])).ToHashSet();

        Assert.Equal([BusinessObjective], TypesIn(Draft));
        Assert.Contains(FR, TypesIn(Gathering));
        Assert.Contains(US, TypesIn(Gathering));
        Assert.Equal([AC], TypesIn(Analysis));
        Assert.Equal([6, 7, 8], TypesIn(Design).Order());
        Assert.Equal([Task], TypesIn(Approved));
        Assert.Equal([TestCase], TypesIn(Testing));
        Assert.Empty(TypesIn(Implementation));
        Assert.Empty(TypesIn(Completed));

        // Each stage lists exactly the artifacts attributed to it.
        foreach (var stage in review["stages"]!.AsArray())
        {
            var expected = artifacts.Where(a => a["createdInStage"]?.GetValue<int>() == Int(stage!["stage"])).Select(a => a["id"]!.GetValue<string>()).Order();
            Assert.Equal(expected, stage!["createdArtifactIds"]!.AsArray().Select(x => x!.GetValue<string>()).Order());
            Assert.Equal(expected.Count(), Int(stage["activity"]!["artifactsCreated"]));
        }
    }

    [Fact]
    public async Task Counts_come_from_the_database()
    {
        var review = await ReviewAsync(_ba);
        var totals = review["totals"]!;

        Assert.Equal(await _host.ScalarAsync($"SELECT COUNT(*) FROM Artifacts WHERE ProjectId = '{_projectId}'"), Int(totals["totalArtifacts"]));
        Assert.Equal(await _host.ScalarAsync($"SELECT COUNT(*) FROM Artifacts WHERE ProjectId = '{_projectId}' AND Origin = 0"), Int(totals["aiGenerated"]));
        Assert.Equal(await _host.ScalarAsync($"SELECT COUNT(*) FROM Artifacts WHERE ProjectId = '{_projectId}' AND Origin = 1"), Int(totals["manual"]));
        Assert.Equal(await _host.ScalarAsync($"SELECT COUNT(*) FROM ArtifactVersions v JOIN Artifacts a ON a.Id = v.ArtifactId WHERE a.ProjectId = '{_projectId}'"), Int(totals["versions"]));
        Assert.Equal(await _host.ScalarAsync($"SELECT COUNT(*) FROM Artifacts WHERE ProjectId = '{_projectId}' AND ArtifactType = {TestCase}"), Int(totals["testsPassed"]));
        Assert.Equal(0, Int(totals["testsFailed"])); // the failed run was superseded by a pass
        Assert.Equal(await _host.ScalarAsync($"SELECT COUNT(*) FROM Artifacts WHERE ProjectId = '{_projectId}' AND ArtifactType = {Task}"), Int(totals["tasksCompleted"]));
        Assert.Equal(await _host.ScalarAsync($"SELECT COUNT(*) FROM Artifacts WHERE ProjectId = '{_projectId}' AND ArtifactType = {FR}"), Int(totals["functionalRequirements"]));
        var byType = totals["byType"]!.AsArray();
        Assert.Equal(Int(totals["totalArtifacts"]), byType.Sum(t => Int(t!["count"])));

        // Work recorded in the stage it happened in, across both Implementation visits.
        Assert.Equal(Int(totals["tasksCompleted"]), Int(Stage(review, Implementation)["activity"]!["tasksCompleted"]));
        Assert.Equal(1, Int(Stage(review, Testing)["activity"]!["testsFailed"]));
        Assert.Equal(Int(totals["testsPassed"]) + 1, Int(Stage(review, Testing)["activity"]!["testsPassed"]));
    }

    [Fact]
    public async Task Provenance_identifies_the_ai_operation_and_distinguishes_manual_artifacts()
    {
        var artifacts = Artifacts(await ReviewAsync(_ba));

        var fr = artifacts.First(a => Int(a["artifactType"]) == FR);
        Assert.Equal(0, Int(fr["origin"]));
        Assert.Equal("RequirementGeneration", fr["ai"]!["operationType"]!.GetValue<string>());
        Assert.Equal(_ba.Id, fr["ai"]!["userId"]!.GetValue<string>());
        Assert.Equal(Gathering, Int(fr["createdInStage"]));
        Assert.Equal(Analysis, Int(fr["servesStage"]));
        Assert.Equal(_ba.Id, fr["createdByUserId"]!.GetValue<string>());

        var test = artifacts.First(a => Int(a["artifactType"]) == TestCase);
        Assert.Equal("TestCaseGeneration", test["ai"]!["operationType"]!.GetValue<string>());
        Assert.Equal(2, Int(test["latestWork"]!["outcome"]));

        var objective = artifacts.Single(a => Int(a["artifactType"]) == BusinessObjective);
        Assert.Equal(1, Int(objective["origin"]));
        Assert.Null(objective["ai"]);
    }

    [Fact]
    public async Task Versions_reviews_and_traceability_links_are_reported()
    {
        var review = await ReviewAsync(_ba);
        var artifacts = Artifacts(review);

        foreach (var a in artifacts.Take(10))
        {
            var id = a["id"]!.GetValue<string>();
            Assert.Equal(await _host.ScalarAsync($"SELECT COUNT(*) FROM ArtifactVersions WHERE ArtifactId = '{id}'"), Int(a["versionCount"]));
        }

        var reviewedFr = artifacts.Single(a => Int(a["reviewCount"]) == 1);
        Assert.Equal(_reviewer.Id, reviewedFr["latestReview"]!["reviewerUserId"]!.GetValue<string>());
        Assert.Equal("Looks right.", reviewedFr["latestReview"]!["comment"]!.GetValue<string>());
        Assert.Equal(1, Int(Stage(review, Analysis)["activity"]!["comments"]));

        var story = artifacts.First(a => Int(a["artifactType"]) == US);
        Assert.Contains(story["links"]!.AsArray(), l => Int(l!["relationshipType"]) == DerivedFrom && l["isOutgoing"]!.GetValue<bool>() && Int(l["artifactType"]) == FR);

        var matrix = review["traceability"]!.AsArray();
        Assert.Equal(Int(review["totals"]!["functionalRequirements"]), matrix.Count);
        Assert.NotNull(review["totals"]!["traceabilityCoveragePercent"]);
    }

    [Fact]
    public async Task Stage_visits_and_decisions_follow_the_recorded_transitions_including_a_rollback()
    {
        var review = await ReviewAsync(_ba);

        var implementation = Stage(review, Implementation)["visits"]!.AsArray();
        Assert.Equal(2, implementation.Count);
        Assert.Equal(1, Int(implementation[0]!["leftBy"])); // first visit ended by advancing to Testing
        Assert.Equal(2, Int(implementation[1]!["enteredBy"]));
        Assert.Equal(Day.AddHours(15), implementation[1]!["enteredAt"]!.GetValue<DateTime>().ToUniversalTime());

        // The Design gate: requested by the Developer, decided by the Reviewer; the stage ends at the decision.
        var design = Stage(review, Design);
        var gate = design["transitions"]!.AsArray().Single()!;
        Assert.Equal(_dev.Id, gate["requestedByUserId"]!.GetValue<string>());
        Assert.Equal(_reviewer.Id, gate["decidedByUserId"]!.GetValue<string>());
        Assert.Equal(Day.AddHours(12).AddMinutes(30), design["completedAt"]!.GetValue<DateTime>().ToUniversalTime());

        var testing = Stage(review, Testing);
        Assert.Equal(2, testing["visits"]!.AsArray().Count);
        Assert.Equal(2, testing["transitions"]!.AsArray().Count); // the rollback and the completion request
    }

    [Fact]
    public async Task Timeline_is_chronological_and_attributes_each_event_to_its_stage()
    {
        var timeline = (await ReviewAsync(_ba))["timeline"]!.AsArray().Select(e => e!).ToList();

        var times = timeline.Select(e => e["at"]!.GetValue<DateTime>()).ToList();
        Assert.Equal(times.Order(), times);

        var rollback = timeline.Single(e => e["title"]!.GetValue<string>() == "Rolled back from Testing to Implementation");
        Assert.Equal(Implementation, Int(rollback["stage"]));
        Assert.Equal("Two tasks were not done.", rollback["detail"]!.GetValue<string>());

        // Requirement generation happened in Requirements Gathering, except the one NFR moved before the
        // recorded history: its event has no stage rather than a guessed one.
        var generation = timeline.Where(e => e["kind"]!.GetValue<int>() == 3 && e["detail"]!.GetValue<string>().StartsWith("Requirement Generation")).ToList();
        Assert.Contains(generation, e => e["title"]!.GetValue<string>().Contains(" Functional Requirement") && Int(e["stage"]) == Gathering && Int(e["count"]) > 0);
        Assert.Single(generation, e => e["stage"] is null && e["title"]!.GetValue<string>() == "AI generated 1 Non-Functional Requirement");

        Assert.Contains(timeline, e => e["title"]!.GetValue<string>() == "Approved the move from Testing to Completed" && e["actorUserId"]!.GetValue<string>() == _reviewer.Id);
        Assert.Contains(timeline, e => e["title"]!.GetValue<string>().StartsWith("Recorded 1 failed test") && Int(e["stage"]) == Testing);
    }

    [Fact]
    public async Task Events_before_recorded_history_are_left_unattributed_instead_of_guessed()
    {
        var review = await ReviewAsync(_ba);

        var early = Artifacts(review).Where(a => a["createdInStage"] is null).ToList();
        Assert.Single(early);
        Assert.Equal(NFR, Int(early[0]["artifactType"]));
        Assert.Equal(1, Int(review["totals"]!["unattributedArtifacts"]));
    }

    [Fact]
    public async Task Project_predating_lifecycle_tracking_reports_missing_history_without_errors()
    {
        var (_, body) = await _ba.Api.PostAsync("/api/projects", new { name = "Legacy", businessProblem = "x", objectives = "y", scope = "z" });
        var legacy = Id(body);
        // As the ProjectLifecycle migration backfilled it: one system Baseline at the then-current stage.
        await SqlAsync($"UPDATE Projects SET Status = {Analysis} WHERE Id = '{legacy}';" +
                       $"UPDATE ProjectStageTransitions SET ToStatus = {Analysis}, RequestedByUserId = 'system', DecidedByUserId = NULL, " +
                       $"RequestedAt = DATEADD(hour, 1, SYSUTCDATETIME()), DecidedAt = DATEADD(hour, 1, SYSUTCDATETIME()) WHERE ProjectId = '{legacy}';");

        var review = await ReviewAsync(_ba, legacy);

        Assert.False(review["historyComplete"]!.GetValue<bool>());
        Assert.False(review["isCompleted"]!.GetValue<bool>());
        Assert.True(Stage(review, Draft)["historyUnavailable"]!.GetValue<bool>());
        Assert.True(Stage(review, Gathering)["historyUnavailable"]!.GetValue<bool>());
        Assert.Equal(1, Int(Stage(review, Analysis)["state"])); // Current
        Assert.All(Artifacts(review), a => Assert.Null(a["createdInStage"]));
        Assert.Contains(review["timeline"]!.AsArray(), e => e!["title"]!.GetValue<string>() == "Lifecycle tracking started at Analysis");
    }

    [Fact]
    public async Task Non_members_are_refused_and_administrators_keep_access()
    {
        var (status, _) = await _outsider.Api.GetAsync($"/api/projects/{_projectId}/lifecycle-review");
        Assert.Equal(403, status);

        var review = await ReviewAsync(_admin); // not a member; Administrator bypasses membership as everywhere else
        Assert.Equal(await _host.ScalarAsync($"SELECT COUNT(*) FROM Artifacts WHERE ProjectId = '{_projectId}'"), Int(review["totals"]!["totalArtifacts"]));

        var (missing, _) = await _admin.Api.GetAsync($"/api/projects/{Guid.NewGuid()}/lifecycle-review");
        Assert.Equal(404, missing);
    }

    [Fact]
    public async Task Developer_sees_only_the_artifacts_their_role_may_see()
    {
        var developerView = await ReviewAsync(_dev);
        var baView = await ReviewAsync(_ba);

        var hidden = Artifacts(baView).Single(a => Int(a["artifactType"]) == FR && Int(a["status"]) == 0)["id"]!.GetValue<string>();
        Assert.DoesNotContain(Artifacts(developerView), a => a["id"]!.GetValue<string>() == hidden);
        Assert.Equal(Int(baView["totals"]!["totalArtifacts"]) - 1, Int(developerView["totals"]!["totalArtifacts"]));
        Assert.DoesNotContain(developerView["timeline"]!.AsArray(), e => e!["artifactIds"]!.AsArray().Any(x => x!.GetValue<string>() == hidden));
        Assert.All(developerView["requirementSources"]!.AsArray(), s => Assert.Null(s!["fileName"]));
    }

    [Fact]
    public async Task Review_is_read_only_and_leaves_lifecycle_and_artifacts_unchanged()
    {
        const string fingerprint =
            "SELECT (SELECT COUNT(*) FROM ProjectStageTransitions) * 1000000 + (SELECT COUNT(*) FROM ArtifactVersions) * 1000 " +
            "+ (SELECT COUNT(*) FROM ArtifactReviews) + (SELECT COUNT(*) FROM AIExecutions) * 7 + (SELECT COUNT(*) FROM ProjectNotifications) * 13";
        var before = await _host.ScalarAsync(fingerprint);
        var (_, historyBefore) = await _ba.Api.GetAsync($"/api/projects/{_projectId}/lifecycle/history");

        await ReviewAsync(_ba);
        await ReviewAsync(_admin);

        Assert.Equal(before, await _host.ScalarAsync(fingerprint));
        var (_, historyAfter) = await _ba.Api.GetAsync($"/api/projects/{_projectId}/lifecycle/history");
        Assert.Equal(historyBefore!.ToJsonString(), historyAfter!.ToJsonString());
        Assert.Equal(Completed, await _host.ScalarAsync($"SELECT Status FROM Projects WHERE Id = '{_projectId}'"));

        // Existing lifecycle and artifact endpoints still answer as before.
        Assert.Equal(200, (await _ba.Api.GetAsync($"/api/projects/{_projectId}/lifecycle")).Status);
        Assert.Equal(200, (await _ba.Api.GetAsync($"/api/projects/{_projectId}/artifacts")).Status);
    }
}
