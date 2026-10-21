using AiReap.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Persistence;

// Application-layer port onto the database, so Application services stay independent of
// the concrete EF Core provider (SQL Server) configured in AiReap.Infrastructure.
public interface IAiReapDbContext
{
    DbSet<Project> Projects { get; }
    DbSet<ProjectStakeholder> ProjectStakeholders { get; }
    DbSet<ProjectMember> ProjectMembers { get; }
    DbSet<RequirementSource> RequirementSources { get; }
    DbSet<Artifact> Artifacts { get; }
    DbSet<ArtifactVersion> ArtifactVersions { get; }
    DbSet<ArtifactRelationship> ArtifactRelationships { get; }
    DbSet<ArtifactReview> ArtifactReviews { get; }
    DbSet<ArtifactImpactNotice> ArtifactImpactNotices { get; }
    DbSet<AIExecution> AIExecutions { get; }
    DbSet<Document> Documents { get; }
    DbSet<DocumentChunk> DocumentChunks { get; }
    DbSet<AgentRun> AgentRuns { get; }
    DbSet<AgentStageRun> AgentStageRuns { get; }
    DbSet<ProjectStageTransition> ProjectStageTransitions { get; }
    DbSet<ProjectStageReview> ProjectStageReviews { get; }
    DbSet<ProjectNotification> ProjectNotifications { get; }
    DbSet<ArtifactWorkRecord> ArtifactWorkRecords { get; }
    DbSet<ArtifactFingerprint> ArtifactFingerprints { get; }
    DbSet<AiChunkRun> AiChunkRuns { get; }
    DbSet<AiChunkRunPart> AiChunkRunParts { get; }

    // S1 - the next SaveChangesAsync writes this tracked artifact's row even when nothing on it
    // changed, under its concurrency tokens: the save fails (DbUpdateConcurrencyException) if the row
    // changed since it was read, and otherwise moves the row's RowVersion on, so a concurrent save
    // that read the row before this one commits fails in turn. For a decision that depended on the
    // artifact's state without changing it (e.g. a requirement checked for promotion and not promoted).
    void MarkArtifactForConcurrencyCheck(Artifact artifact);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
