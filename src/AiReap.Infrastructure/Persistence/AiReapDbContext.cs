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
    public DbSet<AIExecution> AIExecutions => Set<AIExecution>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<AgentStageRun> AgentStageRuns => Set<AgentStageRun>();

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

        builder.Entity<Document>(b =>
        {
            b.HasMany(d => d.Chunks)
                .WithOne(c => c.Document)
                .HasForeignKey(c => c.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
