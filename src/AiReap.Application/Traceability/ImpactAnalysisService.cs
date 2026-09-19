using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Traceability;

public class ImpactAnalysisService : IImpactAnalysisService
{
    private const int MaxHops = 3;

    private readonly IAiReapDbContext _db;

    public ImpactAnalysisService(IAiReapDbContext db)
    {
        _db = db;
    }

    public async Task<ImpactAnalysisResult?> AnalyzeAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == artifactId, cancellationToken);
        if (artifact is null)
        {
            return null;
        }

        var allArtifacts = await _db.Artifacts.Where(a => a.ProjectId == artifact.ProjectId).ToListAsync(cancellationToken);
        var byId = allArtifacts.ToDictionary(a => a.Id);
        var relationships = await _db.ArtifactRelationships
            .Where(r => byId.Keys.Contains(r.SourceArtifactId) || byId.Keys.Contains(r.TargetArtifactId))
            .ToListAsync(cancellationToken);

        // The graph is walked as undirected — relationship direction conventions vary by
        // type (§21 chain direction differs from §15 conflict-pair direction), but for "what
        // might this affect" purposes, any direct link is a candidate regardless of which
        // side is Source vs Target.
        var visited = new HashSet<Guid> { artifact.Id };
        var found = new List<(Artifact Artifact, ArtifactRelationship Edge)>();
        var frontier = new List<Guid> { artifact.Id };

        for (var hop = 0; hop < MaxHops && frontier.Count > 0; hop++)
        {
            var next = new List<Guid>();
            foreach (var currentId in frontier)
            {
                var edges = relationships.Where(r => r.SourceArtifactId == currentId || r.TargetArtifactId == currentId);
                foreach (var edge in edges)
                {
                    var otherId = edge.SourceArtifactId == currentId ? edge.TargetArtifactId : edge.SourceArtifactId;
                    if (visited.Add(otherId) && byId.TryGetValue(otherId, out var otherArtifact))
                    {
                        found.Add((otherArtifact, edge));
                        next.Add(otherId);
                    }
                }
            }

            frontier = next;
        }

        ImpactedArtifact ToImpacted((Artifact Artifact, ArtifactRelationship Edge) x) => new(
            x.Artifact.Id, x.Artifact.Code, x.Artifact.Title, x.Artifact.ArtifactType.ToString(), x.Artifact.Status.ToString(), x.Edge.RelationshipType.ToString());

        var approvedOrLater = found
            .Where(x => x.Artifact.Status is ArtifactStatus.Approved or ArtifactStatus.Implemented or ArtifactStatus.Verified)
            .Select(ToImpacted)
            .ToList();

        var other = found
            .Where(x => x.Artifact.Status is not (ArtifactStatus.Approved or ArtifactStatus.Implemented or ArtifactStatus.Verified))
            .Select(ToImpacted)
            .ToList();

        return new ImpactAnalysisResult(artifact.Id, artifact.Code, artifact.Title, approvedOrLater, other);
    }
}
