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

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
