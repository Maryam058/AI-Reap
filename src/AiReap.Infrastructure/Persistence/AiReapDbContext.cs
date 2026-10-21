using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Infrastructure.Persistence;

public class AiReapDbContext : IdentityDbContext<ApplicationUser>, IAiReapDbContext
{
    public AiReapDbContext(DbContextOptions<AiReapDbContext> options) : base(options)
    {
    }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectStakeholder> ProjectStakeholders => Set<ProjectStakeholder>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<RequirementSource> RequirementSources => Set<RequirementSource>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<ArtifactVersion> ArtifactVersions => Set<ArtifactVersion>();
    public DbSet<ArtifactRelationship> ArtifactRelationships => Set<ArtifactRelationship>();
    public DbSet<ArtifactReview> ArtifactReviews => Set<ArtifactReview>();
    public DbSet<ArtifactImpactNotice> ArtifactImpactNotices => Set<ArtifactImpactNotice>();
    public DbSet<AIExecution> AIExecutions => Set<AIExecution>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<AgentStageRun> AgentStageRuns => Set<AgentStageRun>();
    public DbSet<ProjectStageTransition> ProjectStageTransitions => Set<ProjectStageTransition>();
    public DbSet<ProjectStageReview> ProjectStageReviews => Set<ProjectStageReview>();
    public DbSet<ProjectNotification> ProjectNotifications => Set<ProjectNotification>();
    public DbSet<ArtifactWorkRecord> ArtifactWorkRecords => Set<ArtifactWorkRecord>();
    public DbSet<ArtifactFingerprint> ArtifactFingerprints => Set<ArtifactFingerprint>();
    public DbSet<AiChunkRun> AiChunkRuns => Set<AiChunkRun>();
    public DbSet<AiChunkRunPart> AiChunkRunParts => Set<AiChunkRunPart>();

    // S1 - see IAiReapDbContext. Marking CurrentVersion modified (with its own value) makes EF issue
    // UPDATE [Artifacts] SET [CurrentVersion] = @v WHERE [Id] = @id AND [CurrentVersion] = @v AND
    // [RowVersion] = @rv: no content, status or UpdatedAt change, but a real UPDATE, so the token
    // check runs and SQL Server moves RowVersion on. An artifact already being updated is marked the
    // same way (harmless); a new or deleted one needs nothing (its INSERT/DELETE is the write).
    public void MarkArtifactForConcurrencyCheck(Artifact artifact)
    {
        var entry = Entry(artifact);
        switch (entry.State)
        {
            case EntityState.Unchanged:
            case EntityState.Modified:
                entry.Property(a => a.CurrentVersion).IsModified = true;
                break;
            case EntityState.Detached:
                throw new InvalidOperationException(
                    $"Artifact {artifact.Id} is not tracked by this context, so its concurrency check would not run.");
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Project>(b =>
        {
            b.Property(p => p.Name).IsRequired().HasMaxLength(200);
            b.HasMany(p => p.Stakeholders)
                .WithOne(s => s.Project)
                .HasForeignKey(s => s.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProjectMember>(b =>
        {
            // Restrict, same reason as RequirementSource/AgentRun: Project already reaches other
            // tables through a cascade path (Stakeholders), and a second cascade path to a
            // different table is fine, but membership rows are removed explicitly, not as a
            // side effect of some other delete.
            b.HasOne<Project>().WithMany().HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(m => new { m.ProjectId, m.UserId }).IsUnique();
        });

        builder.Entity<RequirementSource>(b =>
        {
            // Restrict rather than Cascade: Project -> Artifact is already a cascade path,
            // and Project -> RequirementSource -> Artifact would be a second one to the same
            // table, which SQL Server rejects. Deleting a project's requirement sources is
            // an explicit, separate operation.
            b.HasOne(s => s.Project)
                .WithMany()
                .HasForeignKey(s => s.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Artifact>(b =>
        {
            b.Property(a => a.Code).IsRequired().HasMaxLength(50);
            b.Property(a => a.Title).IsRequired().HasMaxLength(300);
            // S1 - an update is made against the row as it was read; a concurrent change to the same
            // artifact fails it (ArtifactConcurrency -> 409) instead of overwriting or duplicating a
            // version. Two tokens: CurrentVersion, the application's version number (moves only on
            // content changes), and RowVersion, SQL Server's rowversion (moves on every UPDATE, so it
            // also catches status-only changes and MarkArtifactForConcurrencyCheck touches).
            b.Property(a => a.CurrentVersion).IsConcurrencyToken();
            b.Property(a => a.RowVersion).IsRowVersion();
            b.Property(a => a.DataJson).HasColumnType("nvarchar(max)");
            // §9 etc.: artifact codes (FR-001, TC-014, ...) are unique within a project+type.
            b.HasIndex(a => new { a.ProjectId, a.ArtifactType, a.Code }).IsUnique();

            b.HasMany(a => a.Versions)
                .WithOne(v => v.Artifact)
                .HasForeignKey(v => v.ArtifactId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasMany(a => a.Reviews)
                .WithOne(r => r.Artifact)
                .HasForeignKey(r => r.ArtifactId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(a => a.RequirementSource)
                .WithMany()
                .HasForeignKey(a => a.RequirementSourceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ArtifactVersion>(b => b.Property(v => v.Title).HasMaxLength(300));

        builder.Entity<ArtifactImpactNotice>(b =>
        {
            b.Property(n => n.Path).IsRequired().HasMaxLength(1000);
            b.Property(n => n.AcknowledgementNote).HasMaxLength(2000);
            // Restrict everywhere (like AgentRuns): avoids SQL Server multiple-cascade-path errors, and a
            // notice is audit history, deleted explicitly rather than as a side effect.
            b.HasOne<Project>().WithMany().HasForeignKey(n => n.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Artifact>().WithMany().HasForeignKey(n => n.SourceArtifactId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Artifact>().WithMany().HasForeignKey(n => n.AffectedArtifactId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(n => new { n.AffectedArtifactId, n.AcknowledgedAt });
            b.HasIndex(n => new { n.ProjectId, n.AcknowledgedAt });
        });

        builder.Entity<ArtifactRelationship>(b =>
        {
            // Restrict, not Cascade: deleting one side of a relationship must not
            // silently cascade-delete the other artifact it's linked to (§22).
            b.HasOne(r => r.SourceArtifact)
                .WithMany()
                .HasForeignKey(r => r.SourceArtifactId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasOne(r => r.TargetArtifact)
                .WithMany()
                .HasForeignKey(r => r.TargetArtifactId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(r => new { r.SourceArtifactId, r.RelationshipType, r.TargetArtifactId }).IsUnique();
        });

        builder.Entity<AgentRun>(b =>
        {
            // Restrict, same reason as RequirementSource: Project already reaches other tables
            // through cascade paths, and runs are deleted explicitly, not as a side effect.
            b.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<RequirementSource>().WithMany().HasForeignKey(r => r.RequirementSourceId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(r => r.Stages)
                .WithOne(s => s.AgentRun)
                .HasForeignKey(s => s.AgentRunId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(r => r.ProjectId);
            // One active run per requirement source, enforced by the database, not just by a
            // check-then-insert in the orchestrator. Active = Running(0), AwaitingApproval(1), Failed(4).
            b.HasIndex(r => r.RequirementSourceId).IsUnique().HasFilter("[Status] IN (0, 1, 4)");
        });

        builder.Entity<AgentStageRun>(b =>
        {
            b.Property(s => s.RowVersion).IsRowVersion();
            b.Property(s => s.OutputJson).HasColumnType("nvarchar(max)");
            b.Property(s => s.Error).HasMaxLength(2000);
            b.Property(s => s.DecisionComment).HasMaxLength(2000);
            b.HasIndex(s => new { s.AgentRunId, s.Order }).IsUnique();
        });

        builder.Entity<ArtifactReview>(b => b.Property(r => r.ReviewerRole).HasMaxLength(64));

        // §5 lifecycle audit/request rows. Restrict to Projects, like every other project-owned
        // history table: they are audit history, never deleted as a side effect.
        builder.Entity<ProjectStageTransition>(b =>
        {
            b.HasOne<Project>().WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.Property(t => t.RowVersion).IsRowVersion();
            b.Property(t => t.RequestedByUserId).IsRequired().HasMaxLength(450);
            b.Property(t => t.DecidedByUserId).HasMaxLength(450);
            b.Property(t => t.RequestedByRole).HasMaxLength(64);
            b.Property(t => t.DecidedByRole).HasMaxLength(64);
            b.Property(t => t.RequestNote).HasMaxLength(2000);
            b.Property(t => t.DecisionReason).HasMaxLength(2000);
            b.Property(t => t.OverrideReason).HasMaxLength(2000);
            b.Property(t => t.EvidenceSnapshotJson).HasColumnType("nvarchar(max)");
            b.Property(t => t.DecisionEvidenceSnapshotJson).HasColumnType("nvarchar(max)");
            b.HasIndex(t => new { t.ProjectId, t.Sequence }).IsUnique();
            // At most one pending (gated) request per project. Pending = 0 (StageTransitionState).
            b.HasIndex(t => t.ProjectId).IsUnique().HasFilter("[State] = 0").HasDatabaseName("IX_ProjectStageTransitions_ProjectId_Pending");
            b.HasMany(t => t.Reviews)
                .WithOne(r => r.Transition)
                .HasForeignKey(r => r.TransitionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProjectStageReview>(b =>
        {
            b.Property(r => r.ReviewerUserId).IsRequired().HasMaxLength(450);
            b.Property(r => r.ReviewerRole).HasMaxLength(64);
            b.Property(r => r.Comment).HasMaxLength(2000);
        });

        builder.Entity<ProjectNotification>(b =>
        {
            b.HasOne<Project>().WithMany().HasForeignKey(n => n.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ProjectStageTransition>().WithMany().HasForeignKey(n => n.TransitionId).OnDelete(DeleteBehavior.Restrict);
            b.Property(n => n.RecipientUserId).IsRequired().HasMaxLength(450);
            b.Property(n => n.Message).IsRequired().HasMaxLength(500);
            // One-time delivery: a transition notifies each recipient at most once per kind.
            b.HasIndex(n => new { n.TransitionId, n.RecipientUserId, n.Kind }).IsUnique();
            b.HasIndex(n => new { n.RecipientUserId, n.AcknowledgedAt });
        });

        builder.Entity<ArtifactWorkRecord>(b =>
        {
            // Cascade like ArtifactReview/ArtifactVersion: work records are part of the artifact's history.
            b.HasOne<Artifact>().WithMany().HasForeignKey(w => w.ArtifactId).OnDelete(DeleteBehavior.Cascade);
            b.Property(w => w.RecordedByUserId).IsRequired().HasMaxLength(450);
            b.Property(w => w.RecordedByRole).HasMaxLength(64);
            b.Property(w => w.Note).HasMaxLength(2000);
            b.HasIndex(w => new { w.ArtifactId, w.RecordedAt });
        });

        // Database-level duplicate guard for AI-created requirements and clarification questions:
        // one artifact per (source, type, normalized content). Only artifacts created after this
        // table exists have a row, so existing artifacts - including earlier duplicates - are never
        // rejected. Deleting the artifact deletes its fingerprint.
        builder.Entity<ArtifactFingerprint>(b =>
        {
            b.HasKey(f => f.ArtifactId);
            b.HasOne<Artifact>().WithOne().HasForeignKey<ArtifactFingerprint>(f => f.ArtifactId).OnDelete(DeleteBehavior.Cascade);
            b.Property(f => f.Fingerprint).IsRequired().HasMaxLength(64);
            b.HasIndex(f => new { f.RequirementSourceId, f.ArtifactType, f.Fingerprint }).IsUnique();
        });

        // Batched AI runs (AiChunkRunService). A run is working state of its requirement source, so
        // it goes with the source (ProjectService.DeleteAsync deletes sources before the project,
        // with no need to know about runs); Restrict to Projects, the only other path.
        builder.Entity<AiChunkRun>(b =>
        {
            b.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<RequirementSource>().WithMany().HasForeignKey(r => r.RequirementSourceId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(r => r.Parts).WithOne(p => p.AiChunkRun).HasForeignKey(p => p.AiChunkRunId).OnDelete(DeleteBehavior.Cascade);
            b.Property(r => r.RowVersion).IsRowVersion();
            b.Property(r => r.PromptTemplateVersion).IsRequired().HasMaxLength(100);
            b.Property(r => r.SystemPrompt).IsRequired().HasColumnType("nvarchar(max)");
            b.Property(r => r.SourceHash).IsRequired().HasMaxLength(64);
            b.Property(r => r.ContextHash).IsRequired().HasMaxLength(64);
            b.Property(r => r.PlanHash).IsRequired().HasMaxLength(64);
            b.Property(r => r.FixedContext).IsRequired().HasColumnType("nvarchar(max)");
            b.Property(r => r.PlanJson).IsRequired().HasColumnType("nvarchar(max)");
            b.Property(r => r.LastError).HasMaxLength(2000);
            b.Property(r => r.ResultJson).HasColumnType("nvarchar(max)");
            b.Property(r => r.StartedByUserId).IsRequired().HasMaxLength(450);
            b.HasIndex(r => r.ProjectId);
            // One active run (Running 0, Staged 1) per source and operation, enforced by the database.
            b.HasIndex(r => new { r.RequirementSourceId, r.Operation }).IsUnique().HasFilter("[Status] IN (0, 1)");
        });

        builder.Entity<AiChunkRunPart>(b =>
        {
            b.Property(p => p.RowVersion).IsRowVersion();
            b.Property(p => p.ChunkHash).IsRequired().HasMaxLength(64);
            b.Property(p => p.SectionPath).HasMaxLength(500);
            b.Property(p => p.SourceIdsJson).IsRequired().HasColumnType("nvarchar(max)");
            b.Property(p => p.PromptText).HasColumnType("nvarchar(max)");
            b.Property(p => p.Model).HasMaxLength(200);
            b.Property(p => p.RawResponse).HasColumnType("nvarchar(max)");
            b.Property(p => p.ParsedJson).HasColumnType("nvarchar(max)");
            b.Property(p => p.LastError).HasMaxLength(2000);
            b.HasIndex(p => new { p.AiChunkRunId, p.PartNumber }).IsUnique();
        });

        builder.Entity<Document>(b =>
        {
            b.HasMany(d => d.Chunks)
                .WithOne(c => c.Document)
                .HasForeignKey(c => c.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
