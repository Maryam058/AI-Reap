using AiReap.Application.Artifacts;
using AiReap.Application.Common;
using AiReap.Domain.Common;
using AiReap.Domain.Enums;
using Xunit;
using static AiReap.Tests.LifecycleTestSupport;

namespace AiReap.Tests;

// Decision 7: Implemented/Verified follow recorded work (a Developer completing a task, QA recording
// a test outcome), not the approval of the task or test case.
public class ArtifactWorkRecordTests : IAsyncLifetime
{
    private const int Approved = 3, Implemented = 5, Verified = 6;

    private readonly TestHost _host = new();
    private Actor _ba = null!, _dev = null!, _qa = null!, _reviewer = null!, _admin = null!;
    private string _frId = "";
    private List<string> _taskIds = [], _testIds = [];

    public async Task InitializeAsync()
    {
        _ba = await _host.ActorAsync(Roles.BusinessAnalyst);
        _dev = await _host.ActorAsync(Roles.Developer);
        _qa = await _host.ActorAsync(Roles.QA);
        _reviewer = await _host.ActorAsync(Roles.Reviewer);
        _admin = await _host.AdminAsync();

        var (_, project) = await _ba.Api.PostAsync("/api/projects", new { name = "Work", description = "d" });
        var projectId = Id(project);
        foreach (var member in new[] { _dev, _qa, _reviewer })
        {
            await _host.AddMemberAsync(_ba.Api, projectId, member.Api);
        }

        var (_, source) = await _ba.Api.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve them." });
        var sourceId = Id(source);
        var (_, gen) = await _ba.Api.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        _frId = Id(gen!["functionalRequirements"]![0]);
        Assert.Equal(200, (await Approve(_frId)).Status);

        var (_, tasks) = await _ba.Api.PostAsync($"/api/requirement-sources/{sourceId}/generate-tasks");
        _taskIds = tasks!.AsArray().Select(Id).ToList();
        var (_, cases) = await _ba.Api.PostAsync($"/api/artifacts/{_frId}/generate-test-cases");
        _testIds = cases!.AsArray().Select(Id).ToList();
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private Task<(int Status, System.Text.Json.Nodes.JsonNode? Body)> Approve(string id) =>
        _reviewer.Api.SendAsync(HttpMethod.Patch, $"/api/artifacts/{id}/status", new { status = Approved });

    private async Task<int> FrStatusAsync() => (await _ba.Api.GetAsync($"/api/artifacts/{_frId}")).Body!["status"]!.GetValue<int>();

    private async Task ApproveAllAsync()
    {
        foreach (var id in _taskIds.Concat(_testIds))
        {
            Assert.Equal(200, (await Approve(id)).Status);
        }
    }

    [Fact]
    public async Task Approving_tasks_and_tests_no_longer_marks_the_requirement_implemented_or_verified()
    {
        await ApproveAllAsync();
        Assert.Equal(Approved, await FrStatusAsync());
        Assert.Equal(0, await _host.ScalarAsync("SELECT COUNT(*) FROM ArtifactWorkRecords"));
    }

    [Fact]
    public async Task Developer_completion_implements_and_qa_passes_verify_without_touching_the_artifacts()
    {
        await ApproveAllAsync();
        var versionsBefore = await _host.ScalarAsync("SELECT COUNT(*) FROM ArtifactVersions");

        using (var dev = _host.As(_dev))
        {
            foreach (var task in _taskIds)
            {
                var record = await dev.Work.RecordAsync(Guid.Parse(task), new RecordWorkRequest(WorkOutcome.TaskCompleted, "Merged in #42"));
                Assert.Equal(Roles.Developer, record.RecordedByRole);
                Assert.Equal(_dev.Id, record.RecordedByUserId);
                Assert.Equal(1, record.VersionNumber);
            }
        }

        Assert.Equal(Implemented, await FrStatusAsync());

        using (var qa = _host.As(_qa))
        {
            await qa.Work.RecordAsync(Guid.Parse(_testIds[0]), new RecordWorkRequest(WorkOutcome.TestPassed, null));
            if (_testIds.Count > 1)
            {
                Assert.Equal(Implemented, await FrStatusAsync()); // not every linked test has passed yet
            }

            foreach (var test in _testIds.Skip(1))
            {
                await qa.Work.RecordAsync(Guid.Parse(test), new RecordWorkRequest(WorkOutcome.TestPassed, null));
            }

            Assert.Equal(_testIds.Count + _taskIds.Count, await _host.ScalarAsync("SELECT COUNT(*) FROM ArtifactWorkRecords"));
            Assert.Single(await qa.Work.GetAsync(Guid.Parse(_testIds[0])));
        }

        Assert.Equal(Verified, await FrStatusAsync());

        // Work records neither version the tasks/tests nor change their approval.
        Assert.Equal(versionsBefore, await _host.ScalarAsync("SELECT COUNT(*) FROM ArtifactVersions"));
        foreach (var id in _taskIds.Concat(_testIds))
        {
            var artifact = (await _ba.Api.GetAsync($"/api/artifacts/{id}")).Body!;
            Assert.Equal(Approved, artifact["status"]!.GetValue<int>());
            Assert.Equal(1, artifact["currentVersion"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task A_failed_test_does_not_verify_the_requirement()
    {
        await ApproveAllAsync();
        using (var dev = _host.As(_dev))
        {
            foreach (var task in _taskIds) await dev.Work.RecordAsync(Guid.Parse(task), new(WorkOutcome.TaskCompleted, null));
        }

        using var qa = _host.As(_qa);
        await qa.Work.RecordAsync(Guid.Parse(_testIds[0]), new(WorkOutcome.TestFailed, "Date validation broken"));
        foreach (var test in _testIds.Skip(1)) await qa.Work.RecordAsync(Guid.Parse(test), new(WorkOutcome.TestPassed, null));
        Assert.Equal(Implemented, await FrStatusAsync());

        // The latest record wins: a later pass verifies it.
        await qa.Work.RecordAsync(Guid.Parse(_testIds[0]), new(WorkOutcome.TestPassed, "Fixed"));
        Assert.Equal(Verified, await FrStatusAsync());
    }

    [Fact]
    public async Task Only_developers_record_task_work_and_only_qa_records_test_outcomes_administrators_may_do_both()
    {
        await ApproveAllAsync();
        var task = Guid.Parse(_taskIds[0]);
        var test = Guid.Parse(_testIds[0]);

        foreach (var actor in new[] { _qa, _ba, _reviewer })
        {
            using var s = _host.As(actor);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Work.RecordAsync(task, new(WorkOutcome.TaskCompleted, null)));
        }

        foreach (var actor in new[] { _dev, _ba, _reviewer })
        {
            using var s = _host.As(actor);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Work.RecordAsync(test, new(WorkOutcome.TestPassed, null)));
        }

        using var admin = _host.As(_admin);
        Assert.Equal(Roles.Administrator, (await admin.Work.RecordAsync(task, new(WorkOutcome.TaskCompleted, null))).RecordedByRole);
        Assert.Equal(Roles.Administrator, (await admin.Work.RecordAsync(test, new(WorkOutcome.TestPassed, null))).RecordedByRole);
    }

    [Fact]
    public async Task Administrator_work_records_are_kept_but_neither_implement_nor_verify_the_requirement()
    {
        await ApproveAllAsync();

        // Administrator completes every task: recorded and visible, but no Developer evidence.
        using (var admin = _host.As(_admin))
        {
            foreach (var task in _taskIds) await admin.Work.RecordAsync(Guid.Parse(task), new(WorkOutcome.TaskCompleted, null));
        }

        Assert.Equal(Approved, await FrStatusAsync());
        using (var dev = _host.As(_dev))
        {
            var record = Assert.Single(await dev.Work.GetAsync(Guid.Parse(_taskIds[0])));
            Assert.Equal(Roles.Administrator, record.RecordedByRole);
            Assert.Equal(_admin.Id, record.RecordedByUserId);

            foreach (var task in _taskIds) await dev.Work.RecordAsync(Guid.Parse(task), new(WorkOutcome.TaskCompleted, null));
        }

        Assert.Equal(Implemented, await FrStatusAsync());

        // Administrator passes every test: no QA evidence, so the requirement is not verified.
        using (var admin = _host.As(_admin))
        {
            foreach (var test in _testIds) await admin.Work.RecordAsync(Guid.Parse(test), new(WorkOutcome.TestPassed, null));
        }

        Assert.Equal(Implemented, await FrStatusAsync());
        using (var qa = _host.As(_qa))
        {
            foreach (var test in _testIds) await qa.Work.RecordAsync(Guid.Parse(test), new(WorkOutcome.TestPassed, null));
        }

        Assert.Equal(Verified, await FrStatusAsync());
        Assert.Equal(2 * (_taskIds.Count + _testIds.Count), await _host.ScalarAsync("SELECT COUNT(*) FROM ArtifactWorkRecords"));
    }

    [Fact]
    public async Task Work_needs_an_approved_artifact_and_an_outcome_that_fits_its_type()
    {
        using var dev = _host.As(_dev);
        using var qa = _host.As(_qa);

        // Not yet approved.
        await Assert.ThrowsAsync<ConflictException>(() => dev.Work.RecordAsync(Guid.Parse(_taskIds[0]), new(WorkOutcome.TaskCompleted, null)));

        await ApproveAllAsync();
        await Assert.ThrowsAsync<RequestValidationException>(() => dev.Work.RecordAsync(Guid.Parse(_taskIds[0]), new(WorkOutcome.TestPassed, null)));
        await Assert.ThrowsAsync<RequestValidationException>(() => qa.Work.RecordAsync(Guid.Parse(_testIds[0]), new(WorkOutcome.TaskCompleted, null)));
        await Assert.ThrowsAsync<RequestValidationException>(() => dev.Work.RecordAsync(Guid.Parse(_frId), new(WorkOutcome.TaskCompleted, null)));
        Assert.Equal(0, await _host.ScalarAsync("SELECT COUNT(*) FROM ArtifactWorkRecords"));
    }
}
