#pragma warning disable EF1001 // IMigrationsModelDiffer / IModelRuntimeInitializer are EF's design-time seams; used here only to compare models.
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using AiReap.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace AiReap.Tests;

// The migration chain, its designer models and AiReapDbContextModelSnapshot against the model
// AiReapDbContext builds - what `dotnet ef migrations add` would compare, without the tool.
//
// Offline: the context is configured for SQL Server so the relational model matches production, but
// no connection is ever opened (nothing here queries or migrates). Nothing is applied to a database.
public class MigrationModelConsistencyTests
{
    private const string D3Migration = "20261019120000_AddDecisionEvidenceSnapshot";
    private const string S1Migration = "20261019120100_ArtifactCurrentVersionConcurrencyToken";
    private const string RowVersionMigration = "20261020090000_AddArtifactRowVersion";
    private const string SourceFilesMigration = "20261022075854_AddRequirementSourceFiles";

    private static AiReapDbContext Context() =>
        new(new DbContextOptionsBuilder<AiReapDbContext>()
            .UseSqlServer("Server=offline-model-check;Database=none;Integrated Security=true;Connect Timeout=1")
            .Options);

    private static IRelationalModel Relational(DbContext context, IModel model)
    {
        if (model is IMutableModel mutable)
        {
            model = mutable.FinalizeModel();
        }

        return context.GetService<IModelRuntimeInitializer>().Initialize(model, designTime: true, validationLogger: null).GetRelationalModel();
    }

    private static IModel TargetModel(DbContext context, string id) =>
        context.GetService<IMigrationsAssembly>().CreateMigration(
            context.GetService<IMigrationsAssembly>().Migrations[id], context.Database.ProviderName!).TargetModel!;

    [Fact]
    public void The_snapshot_matches_the_current_model()
    {
        using var context = Context();
        var snapshot = Relational(context, context.GetService<IMigrationsAssembly>().ModelSnapshot!.Model);
        var current = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        Assert.Empty(context.GetService<IMigrationsModelDiffer>().GetDifferences(snapshot, current));
    }

    [Fact]
    public void The_latest_migration_target_model_matches_the_snapshot()
    {
        using var context = Context();
        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order(StringComparer.Ordinal).ToList();
        Assert.Equal(SourceFilesMigration, migrations[^1]);
        Assert.Equal(RowVersionMigration, migrations[^2]);
        Assert.Equal(S1Migration, migrations[^3]);
        Assert.Equal(D3Migration, migrations[^4]);

        var snapshot = Relational(context, context.GetService<IMigrationsAssembly>().ModelSnapshot!.Model);
        var latest = Relational(context, TargetModel(context, SourceFilesMigration));
        Assert.Empty(context.GetService<IMigrationsModelDiffer>().GetDifferences(latest, snapshot));
    }

    // Original source documents (docs/SOURCE_DOCUMENT_RETENTION.md): one new table and its index, and
    // nothing else - no existing table or column is touched.
    [Fact]
    public void AddRequirementSourceFiles_only_creates_the_source_files_table()
    {
        using var context = Context();

        var differences = context.GetService<IMigrationsModelDiffer>().GetDifferences(
            Relational(context, TargetModel(context, RowVersionMigration)), Relational(context, TargetModel(context, SourceFilesMigration)));

        var create = Assert.IsType<CreateTableOperation>(Assert.Single(differences, d => d is CreateTableOperation));
        Assert.Equal("RequirementSourceFiles", create.Name);
        var foreignKey = Assert.Single(create.ForeignKeys);
        Assert.Equal(("RequirementSources", ReferentialAction.Cascade), (foreignKey.PrincipalTable, foreignKey.OnDelete));
        var index = Assert.IsType<CreateIndexOperation>(Assert.Single(differences, d => d is not CreateTableOperation));
        Assert.Equal(("RequirementSourceFiles", true), (index.Table, index.IsUnique));
    }

    [Fact]
    public void D3_adds_only_the_nullable_decision_evidence_column()
    {
        using var context = Context();
        var assembly = context.GetService<IMigrationsAssembly>();
        var migrations = assembly.Migrations.Keys.Order(StringComparer.Ordinal).ToList();
        var previous = migrations[migrations.IndexOf(D3Migration) - 1];

        var differences = context.GetService<IMigrationsModelDiffer>().GetDifferences(
            Relational(context, TargetModel(context, previous)), Relational(context, TargetModel(context, D3Migration)));
        var add = Assert.IsType<AddColumnOperation>(Assert.Single(differences));
        Assert.Equal(("ProjectStageTransitions", nameof(ProjectStageTransition.DecisionEvidenceSnapshotJson), "nvarchar(max)", true),
            (add.Table, add.Name, add.ColumnType, add.IsNullable));

        // The migration's own Up/Down do exactly that, and nothing else.
        var migration = assembly.CreateMigration(assembly.Migrations[D3Migration], context.Database.ProviderName!);
        var up = Assert.IsType<AddColumnOperation>(Assert.Single(migration.UpOperations));
        Assert.Equal((add.Table, add.Name, add.ColumnType, add.IsNullable), (up.Table, up.Name, up.ColumnType, up.IsNullable));
        var down = Assert.IsType<DropColumnOperation>(Assert.Single(migration.DownOperations));
        Assert.Equal((add.Table, add.Name), (down.Table, down.Name));

        // At D3 CurrentVersion is not yet a concurrency token; that is S1's (schema-free) change.
        Assert.False(TargetModel(context, D3Migration).FindEntityType(typeof(Artifact))!.FindProperty(nameof(Artifact.CurrentVersion))!.IsConcurrencyToken);
    }

    [Fact]
    public void S1_marks_CurrentVersion_as_a_concurrency_token_without_a_schema_change()
    {
        using var context = Context();
        var assembly = context.GetService<IMigrationsAssembly>();

        var migration = assembly.CreateMigration(assembly.Migrations[S1Migration], context.Database.ProviderName!);
        Assert.Empty(migration.UpOperations);
        Assert.Empty(migration.DownOperations);
        Assert.Empty(context.GetService<IMigrationsModelDiffer>().GetDifferences(
            Relational(context, TargetModel(context, D3Migration)), Relational(context, TargetModel(context, S1Migration))));

        bool Token(IModel model) => model.FindEntityType(typeof(Artifact))!.FindProperty(nameof(Artifact.CurrentVersion))!.IsConcurrencyToken;
        Assert.True(Token(TargetModel(context, S1Migration)));
        Assert.True(Token(assembly.ModelSnapshot!.Model));
        Assert.True(Token(context.Model));
        // No rowversion column was added by S1 itself (that is AddArtifactRowVersion, below).
        Assert.DoesNotContain(TargetModel(context, S1Migration).FindEntityType(typeof(Artifact))!.GetProperties(), p => p.IsConcurrencyToken && p.Name != nameof(Artifact.CurrentVersion));
    }

    // ---- S1 remediation: Artifacts.RowVersion -------------------------------------------------------------
    // Static checks of the model, the snapshot, the migration and the SQL EF would generate. Nothing is
    // applied to or run against a database: whether SQL Server enforces the token in a real race is
    // not verified here.

    [Fact]
    public void RowVersion_is_a_database_generated_rowversion_in_the_model_the_snapshot_and_the_migration()
    {
        using var context = Context();
        var assembly = context.GetService<IMigrationsAssembly>();

        foreach (var (name, model) in new[]
        {
            ("context", (IReadOnlyModel)context.GetService<IDesignTimeModel>().Model),
            ("snapshot", assembly.ModelSnapshot!.Model),
            ("migration", TargetModel(context, RowVersionMigration))
        })
        {
            var artifact = model.FindEntityType(typeof(Artifact).FullName!)!;
            var rowVersion = artifact.FindProperty(nameof(Artifact.RowVersion));
            Assert.True(rowVersion is not null, name);
            Assert.Equal(typeof(byte[]), rowVersion!.ClrType);
            Assert.True(rowVersion.IsConcurrencyToken, name);
            Assert.Equal(ValueGenerated.OnAddOrUpdate, rowVersion.ValueGenerated);
            Assert.False(rowVersion.IsNullable, name);
            Assert.Equal("rowversion", rowVersion.GetColumnType());

            // CurrentVersion keeps its own concurrency token, beside RowVersion.
            Assert.True(artifact.FindProperty(nameof(Artifact.CurrentVersion))!.IsConcurrencyToken, name);
            Assert.Equal(
                new[] { nameof(Artifact.CurrentVersion), nameof(Artifact.RowVersion) },
                artifact.GetProperties().Where(p => p.IsConcurrencyToken).Select(p => p.Name).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void AddArtifactRowVersion_adds_only_the_RowVersion_column_without_a_default()
    {
        using var context = Context();
        var assembly = context.GetService<IMigrationsAssembly>();

        // Model to model: S1's target -> this migration's target differs by exactly one column.
        var differences = context.GetService<IMigrationsModelDiffer>().GetDifferences(
            Relational(context, TargetModel(context, S1Migration)), Relational(context, TargetModel(context, RowVersionMigration)));
        var diff = Assert.IsType<AddColumnOperation>(Assert.Single(differences));
        Assert.Equal(("Artifacts", nameof(Artifact.RowVersion), "rowversion", false, true),
            (diff.Table, diff.Name, diff.ColumnType, diff.IsNullable, diff.IsRowVersion));

        // The migration's own Up/Down: that column and nothing else, and no default value - SQL Server
        // fills a new rowversion column itself and does not accept a default on one.
        var migration = assembly.CreateMigration(assembly.Migrations[RowVersionMigration], context.Database.ProviderName!);
        var up = Assert.IsType<AddColumnOperation>(Assert.Single(migration.UpOperations));
        Assert.Equal((diff.Table, diff.Name, diff.ColumnType, diff.IsNullable, diff.IsRowVersion), (up.Table, up.Name, up.ColumnType, up.IsNullable, up.IsRowVersion));
        Assert.Equal(typeof(byte[]), up.ClrType);
        Assert.Null(up.DefaultValue);
        Assert.Null(up.DefaultValueSql);
        Assert.Null(up.ComputedColumnSql);
        var down = Assert.IsType<DropColumnOperation>(Assert.Single(migration.DownOperations));
        Assert.Equal(("Artifacts", nameof(Artifact.RowVersion)), (down.Table, down.Name));

        // The SQL EF would generate for Up (generated as text only; nothing is executed), against the
        // target model finalized and initialized as the migrator does.
        var target = migration.TargetModel is IMutableModel mutable ? mutable.FinalizeModel() : migration.TargetModel!;
        target = context.GetService<IModelRuntimeInitializer>().Initialize(target, designTime: true, validationLogger: null);
        var sql = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, target)).CommandText;
        Assert.Contains("ALTER TABLE [Artifacts] ADD [RowVersion] rowversion NOT NULL", sql);
        Assert.DoesNotContain("DEFAULT", sql, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the UPDATE a concurrency touch produces (command shape, offline) ----------------------------------
    // The changes are tracked and handed to EF's own command batching and SQL Server UPDATE generator, then
    // inspected; SaveChanges is never called. The context refuses to open a connection, so a mistake here
    // fails the test instead of reaching a server.

    private sealed class NoConnection : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
    {
        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult ConnectionOpening(
            System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result) =>
            throw new InvalidOperationException("These tests must not open a database connection.");

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> ConnectionOpeningAsync(
            System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("These tests must not open a database connection.");
    }

    private static AiReapDbContext OfflineContext() =>
        new(new DbContextOptionsBuilder<AiReapDbContext>()
            .UseSqlServer("Server=offline-model-check;Database=none;Integrated Security=true;Connect Timeout=1")
            .AddInterceptors(new NoConnection())
            .Options);

    private static Artifact Stored(string code, ArtifactStatus status = ArtifactStatus.Approved) => new()
    {
        Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), ArtifactType = ArtifactType.FunctionalRequirement, Code = code, Title = code,
        Status = status, DataJson = "{}", CurrentVersion = 3, CreatedByUserId = "ba", RowVersion = [0, 0, 0, 0, 0, 0, 0, 42]
    };

    private static List<Microsoft.EntityFrameworkCore.Update.IReadOnlyModificationCommand> Commands(AiReapDbContext context)
    {
        var entries = context.GetService<Microsoft.EntityFrameworkCore.ChangeTracking.Internal.IStateManager>().GetEntriesToSave(cascadeChanges: true);
        var adapter = context.GetService<Microsoft.EntityFrameworkCore.Update.IUpdateAdapterFactory>().Create();
        return context.GetService<Microsoft.EntityFrameworkCore.Update.ICommandBatchPreparer>()
            .BatchCommands(entries, adapter)
            .SelectMany(b => b.ModificationCommands)
            .ToList();
    }

    private static string[] Columns(Microsoft.EntityFrameworkCore.Update.IReadOnlyModificationCommand command, Func<Microsoft.EntityFrameworkCore.Update.IColumnModification, bool> which) =>
        command.ColumnModifications.Where(which).Select(c => c.ColumnName).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void A_concurrency_touch_on_an_unchanged_artifact_is_an_UPDATE_under_both_tokens()
    {
        using var context = OfflineContext();
        var artifact = Stored("FR-001");
        context.Artifacts.Attach(artifact); // as if just read: Unchanged
        context.ArtifactWorkRecords.Add(new ArtifactWorkRecord
        {
            Id = Guid.NewGuid(), ArtifactId = Guid.NewGuid(), VersionNumber = 1, Outcome = WorkOutcome.TaskCompleted,
            RecordedByUserId = "dev", RecordedByRole = "Developer", RecordedAt = DateTime.UtcNow
        });

        context.MarkArtifactForConcurrencyCheck(artifact);

        var update = Assert.Single(Commands(context), c => c.TableName == "Artifacts");
        Assert.Equal(EntityState.Modified, update.EntityState);
        // Writes only CurrentVersion (its own value): no content, status or UpdatedAt change.
        Assert.Equal(new[] { nameof(Artifact.CurrentVersion) }, Columns(update, c => c.IsWrite));
        Assert.Equal(3, update.ColumnModifications.Single(c => c.IsWrite).Value);
        // ...under the key and both tokens, as read; SQL Server then returns the new RowVersion.
        Assert.Equal(new[] { nameof(Artifact.CurrentVersion), nameof(Artifact.Id), nameof(Artifact.RowVersion) }, Columns(update, c => c.IsCondition));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 42 }, (byte[])update.ColumnModifications.Single(c => c.IsCondition && c.ColumnName == nameof(Artifact.RowVersion)).OriginalValue!);
        Assert.Equal(new[] { nameof(Artifact.RowVersion) }, Columns(update, c => c.IsRead));
        Assert.Single(Commands(context), c => c.TableName == "ArtifactWorkRecords" && c.EntityState == EntityState.Added);

        // The statement SQL Server's update generator writes for it (text only; not executed).
        var sql = new System.Text.StringBuilder();
        context.GetService<Microsoft.EntityFrameworkCore.Update.IUpdateSqlGenerator>().AppendUpdateOperation(sql, update, 0, out _);
        var text = sql.ToString();
        Assert.Contains("UPDATE [Artifacts] SET [CurrentVersion] = @p", text);
        Assert.Matches(@"WHERE \[Id\] = @p\d+ AND \[CurrentVersion\] = @p\d+ AND \[RowVersion\] = @p\d+", text);
        Assert.Contains("[RowVersion]", text[text.IndexOf("OUTPUT", StringComparison.Ordinal)..]);
        Assert.DoesNotContain("[Status]", text);
        Assert.DoesNotContain("[DataJson]", text);
        Assert.DoesNotContain("[UpdatedAt]", text);
    }

    [Fact]
    public void A_concurrency_touch_on_a_promoted_artifact_keeps_its_real_changes_and_both_tokens()
    {
        using var context = OfflineContext();
        var artifact = Stored("FR-002");
        context.Artifacts.Attach(artifact);
        artifact.Status = ArtifactStatus.Implemented;
        artifact.UpdatedAt = DateTime.UtcNow;
        context.ChangeTracker.DetectChanges();

        context.MarkArtifactForConcurrencyCheck(artifact);

        var update = Assert.Single(Commands(context));
        Assert.Equal(new[] { nameof(Artifact.CurrentVersion), nameof(Artifact.Status), nameof(Artifact.UpdatedAt) }, Columns(update, c => c.IsWrite));
        Assert.Equal(new[] { nameof(Artifact.CurrentVersion), nameof(Artifact.Id), nameof(Artifact.RowVersion) }, Columns(update, c => c.IsCondition));
    }

    [Fact]
    public void A_concurrency_touch_needs_a_tracked_artifact_and_is_undone_with_the_save()
    {
        using var context = OfflineContext();
        Assert.Throws<InvalidOperationException>(() => context.MarkArtifactForConcurrencyCheck(Stored("FR-003")));

        // ArtifactPersistence.Undo (run on a conflict) puts a touched artifact back to Unchanged.
        var artifact = Stored("FR-004");
        context.Artifacts.Attach(artifact);
        context.MarkArtifactForConcurrencyCheck(artifact);
        Assert.Equal(EntityState.Modified, context.Entry(artifact).State);
        AiReap.Application.Artifacts.ArtifactPersistence.Undo(context);
        Assert.Equal(EntityState.Unchanged, context.Entry(artifact).State);
        Assert.Empty(Commands(context));
    }
}
