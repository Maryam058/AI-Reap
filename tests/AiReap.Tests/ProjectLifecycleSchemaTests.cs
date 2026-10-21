using AiReap.Domain.Common;
using AiReap.Domain.Enums;
using AiReap.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace AiReap.Tests;

// §5 lifecycle alignment, phase 1: the lifecycle policy table and the ProjectLifecycle migration.
public class ProjectLifecyclePolicyTests
{
    private static Func<string, bool> As(params string[] roles) => roles.Contains;

    [Fact]
    public void Lifecycle_is_the_eight_srs_stages_in_order_with_their_owners()
    {
        Assert.Equal(
            [ProjectStatus.Draft, ProjectStatus.RequirementsGathering, ProjectStatus.Analysis, ProjectStatus.Design,
             ProjectStatus.Approved, ProjectStatus.Implementation, ProjectStatus.Testing, ProjectStatus.Completed],
            ProjectLifecyclePolicy.Stages.Select(s => s.Status));

        Assert.Equal(
            [Roles.BusinessAnalyst, Roles.BusinessAnalyst, Roles.BusinessAnalyst, Roles.Developer,
             Roles.Reviewer, Roles.Developer, Roles.QA, Roles.Reviewer],
            ProjectLifecyclePolicy.Stages.Select(s => s.OwnerRole));
    }

    [Fact]
    public void Advance_is_exactly_one_stage_forward_and_nothing_follows_completed()
    {
        foreach (var (stage, i) in ProjectLifecyclePolicy.Stages.Select((s, i) => (s.Status, i)))
        {
            foreach (var target in Enum.GetValues<ProjectStatus>())
            {
                var expected = ProjectLifecyclePolicy.IndexOf(target) == i + 1;
                Assert.Equal(expected, ProjectLifecyclePolicy.IsLegalAdvance(stage, target));
            }
        }

        Assert.Null(ProjectLifecyclePolicy.Next(ProjectStatus.Completed));
        Assert.True(ProjectLifecyclePolicy.IsFinal(ProjectStatus.Completed));
    }

    [Fact]
    public void Rollback_targets_only_earlier_stages()
    {
        Assert.True(ProjectLifecyclePolicy.IsLegalRollback(ProjectStatus.Testing, ProjectStatus.Design));
        Assert.True(ProjectLifecyclePolicy.IsLegalRollback(ProjectStatus.RequirementsGathering, ProjectStatus.Draft));
        Assert.False(ProjectLifecyclePolicy.IsLegalRollback(ProjectStatus.Design, ProjectStatus.Design));
        Assert.False(ProjectLifecyclePolicy.IsLegalRollback(ProjectStatus.Design, ProjectStatus.Approved));
    }

    [Fact]
    public void Only_design_and_testing_are_gated()
    {
        var gated = ProjectLifecyclePolicy.Stages.Where(s => ProjectLifecyclePolicy.IsGated(s.Status)).Select(s => s.Status);
        Assert.Equal([ProjectStatus.Design, ProjectStatus.Testing], gated);
    }

    [Theory]
    [InlineData(ProjectStatus.Draft, Roles.BusinessAnalyst, Roles.BusinessAnalyst)]
    [InlineData(ProjectStatus.Analysis, Roles.BusinessAnalyst, Roles.BusinessAnalyst)]
    [InlineData(ProjectStatus.Design, Roles.Developer, Roles.Developer)]
    [InlineData(ProjectStatus.Approved, Roles.Reviewer, Roles.Reviewer)]
    [InlineData(ProjectStatus.Implementation, Roles.Developer, Roles.Developer)]
    [InlineData(ProjectStatus.Testing, Roles.QA, Roles.QA)]
    [InlineData(ProjectStatus.Draft, Roles.Administrator, Roles.Administrator)]
    [InlineData(ProjectStatus.Testing, Roles.Administrator, Roles.Administrator)]
    [InlineData(ProjectStatus.Draft, Roles.Reviewer, null)]
    [InlineData(ProjectStatus.Design, Roles.BusinessAnalyst, null)]
    [InlineData(ProjectStatus.Design, Roles.Reviewer, null)]
    [InlineData(ProjectStatus.Implementation, Roles.QA, null)]
    [InlineData(ProjectStatus.Testing, Roles.Developer, null)]
    [InlineData(ProjectStatus.Completed, Roles.Administrator, null)]
    [InlineData(ProjectStatus.Completed, Roles.Reviewer, null)]
    public void Only_the_stage_owner_or_an_administrator_may_advance(ProjectStatus from, string role, string? actsAs)
    {
        Assert.Equal(actsAs, ProjectLifecyclePolicy.AdvanceRole(from, As(role)));
    }

    [Fact]
    public void An_owner_who_is_also_an_administrator_is_recorded_under_the_owner_role()
    {
        Assert.Equal(Roles.Developer, ProjectLifecyclePolicy.AdvanceRole(ProjectStatus.Design, As(Roles.Administrator, Roles.Developer)));
    }

    [Theory]
    [InlineData(ProjectStatus.Design, Roles.Reviewer, Roles.Reviewer)]
    [InlineData(ProjectStatus.Testing, Roles.Reviewer, Roles.Reviewer)]
    [InlineData(ProjectStatus.Design, Roles.Administrator, Roles.Administrator)]
    [InlineData(ProjectStatus.Design, Roles.Developer, null)]
    [InlineData(ProjectStatus.Testing, Roles.QA, null)]
    [InlineData(ProjectStatus.Testing, Roles.BusinessAnalyst, null)]
    [InlineData(ProjectStatus.Analysis, Roles.Reviewer, null)]
    public void Only_a_reviewer_or_administrator_decides_a_gate(ProjectStatus from, string role, string? actsAs)
    {
        Assert.Equal(actsAs, ProjectLifecyclePolicy.GateDecisionRole(from, As(role)));
    }

    [Fact]
    public void Rollback_and_override_are_administrator_only()
    {
        foreach (var role in Roles.All.Where(r => r != Roles.Administrator))
        {
            Assert.Null(ProjectLifecyclePolicy.RollbackRole(As(role)));
            Assert.Null(ProjectLifecyclePolicy.OverrideRole(As(role)));
        }

        Assert.Equal(Roles.Administrator, ProjectLifecyclePolicy.RollbackRole(As(Roles.Administrator)));
        Assert.Equal(Roles.Administrator, ProjectLifecyclePolicy.OverrideRole(As(Roles.Administrator)));
    }
}

public class ProjectLifecycleMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20260924114054_ImpactNoticesAndBusinessObjectives";

    private readonly TestHost _host = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    [Fact]
    public async Task Migration_creates_lifecycle_tables_keys_and_guards()
    {
        Assert.Equal(4, await _host.ScalarAsync(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME IN ('ProjectStageTransitions','ProjectStageReviews','ProjectNotifications','ArtifactWorkRecords')"));

        var fkSql = @"SELECT COUNT(*) FROM sys.foreign_keys fk
            JOIN sys.tables t ON fk.parent_object_id = t.object_id
            JOIN sys.tables r ON fk.referenced_object_id = r.object_id
            WHERE t.name = '{0}' AND r.name = '{1}' AND fk.delete_referential_action_desc = '{2}'";
        Assert.Equal(1, await _host.ScalarAsync(string.Format(fkSql, "ProjectStageTransitions", "Projects", "NO_ACTION")));
        Assert.Equal(1, await _host.ScalarAsync(string.Format(fkSql, "ProjectStageReviews", "ProjectStageTransitions", "CASCADE")));
        Assert.Equal(1, await _host.ScalarAsync(string.Format(fkSql, "ProjectNotifications", "Projects", "NO_ACTION")));
        Assert.Equal(1, await _host.ScalarAsync(string.Format(fkSql, "ProjectNotifications", "ProjectStageTransitions", "NO_ACTION")));
        Assert.Equal(1, await _host.ScalarAsync(string.Format(fkSql, "ArtifactWorkRecords", "Artifacts", "CASCADE")));

        // One pending request per project; one ordered sequence per project; one-time notifications.
        Assert.Equal(1, await _host.ScalarAsync(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_ProjectStageTransitions_ProjectId_Pending' AND is_unique = 1 AND has_filter = 1"));
        Assert.Equal(1, await _host.ScalarAsync(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_ProjectStageTransitions_ProjectId_Sequence' AND is_unique = 1"));
        Assert.Equal(1, await _host.ScalarAsync(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_ProjectNotifications_TransitionId_RecipientUserId_Kind' AND is_unique = 1"));
        Assert.Equal(1, await _host.ScalarAsync(
            "SELECT COUNT(*) FROM sys.columns c JOIN sys.types t ON c.user_type_id = t.user_type_id WHERE c.object_id = OBJECT_ID('ProjectStageTransitions') AND c.name = 'RowVersion' AND t.name = 'timestamp'"));

        // Additive, nullable columns on existing tables.
        Assert.Equal(1, await _host.ScalarAsync(
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('ArtifactReviews') AND name = 'ReviewerRole' AND is_nullable = 1"));
        Assert.Equal(1, await _host.ScalarAsync(
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('AgentRuns') AND name = 'ProjectStatusAtStart' AND is_nullable = 1"));
    }

    [Fact]
    public async Task Existing_projects_get_one_baseline_row_and_keep_their_status()
    {
        // Created and dropped only by the guard. Declared in this order so the verified connection is
        // disposed (releasing its reservation) before the database's guarded drop runs.
        await using var database = await TestSqlServer.Default.CreateDatabaseAsync();
        await using var conn = await TestSqlServer.Default.OpenVerifiedAsync(database.Name);

        await using (var db = new AiReapDbContext(conn.CreateMigrationOptions()))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            // Two projects that predate lifecycle tracking, one far along the lifecycle.
            await db.Database.ExecuteSqlRawAsync(@"
                INSERT INTO Projects (Id, Name, Status, CreatedByUserId, CreatedAt, UpdatedAt)
                VALUES ('11111111-1111-1111-1111-111111111111', 'Old draft', 0, 'u1', SYSUTCDATETIME(), SYSUTCDATETIME()),
                       ('22222222-2222-2222-2222-222222222222', 'Old implementation', 5, 'u1', SYSUTCDATETIME(), SYSUTCDATETIME())");

            await db.Database.MigrateAsync();
        }

        async Task<int> Scalar(string sql)
        {
            await using var cmd = conn.CreateCommand(sql);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        // Status untouched: nobody is moved backward.
        Assert.Equal(5, await Scalar("SELECT Status FROM Projects WHERE Id = '22222222-2222-2222-2222-222222222222'"));
        Assert.Equal(0, await Scalar("SELECT Status FROM Projects WHERE Id = '11111111-1111-1111-1111-111111111111'"));

        // Exactly one Baseline(0), Completed(1) row per project, at its current status, sequence 0.
        Assert.Equal(2, await Scalar("SELECT COUNT(*) FROM ProjectStageTransitions"));
        Assert.Equal(1, await Scalar(@"SELECT COUNT(*) FROM ProjectStageTransitions
            WHERE ProjectId = '22222222-2222-2222-2222-222222222222' AND Kind = 0 AND State = 1
              AND FromStatus IS NULL AND ToStatus = 5 AND Sequence = 0 AND IsOverride = 0 AND RequestedByUserId = 'system'"));
        Assert.Equal(1, await Scalar(@"SELECT COUNT(*) FROM ProjectStageTransitions
            WHERE ProjectId = '11111111-1111-1111-1111-111111111111' AND Kind = 0 AND ToStatus = 0"));
    }
}
