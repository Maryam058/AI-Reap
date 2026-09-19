using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Traceability;

public class TraceabilityService : ITraceabilityService
{
    private readonly IAiReapDbContext _db;

    public TraceabilityService(IAiReapDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<TraceabilityRow>> GetMatrixAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var artifacts = await _db.Artifacts.Where(a => a.ProjectId == projectId).ToListAsync(cancellationToken);
        var relationships = await _db.ArtifactRelationships
            .Where(r => artifacts.Select(a => a.Id).Contains(r.SourceArtifactId) || artifacts.Select(a => a.Id).Contains(r.TargetArtifactId))
            .ToListAsync(cancellationToken);

        var byId = artifacts.ToDictionary(a => a.Id);
        var outgoing = relationships.GroupBy(r => r.SourceArtifactId).ToDictionary(g => g.Key, g => g.ToList());
        var incoming = relationships.GroupBy(r => r.TargetArtifactId).ToDictionary(g => g.Key, g => g.ToList());

        List<ArtifactRelationship> Incoming(Guid id, RelationshipType type) =>
            incoming.TryGetValue(id, out var list) ? list.Where(r => r.RelationshipType == type).ToList() : new();

        List<ArtifactRelationship> Outgoing(Guid id, RelationshipType type) =>
            outgoing.TryGetValue(id, out var list) ? list.Where(r => r.RelationshipType == type).ToList() : new();

        TraceRef ToRef(Artifact a) => new(a.Id, a.Code, a.Title);

        var rows = new List<TraceabilityRow>();

        foreach (var fr in artifacts.Where(a => a.ArtifactType == ArtifactType.FunctionalRequirement).OrderBy(a => a.Code))
        {
            var businessRules = Incoming(fr.Id, RelationshipType.LinkedRule).Select(r => byId[r.SourceArtifactId]);
            var userStories = Incoming(fr.Id, RelationshipType.DerivedFrom)
                .Select(r => byId[r.SourceArtifactId]).Where(a => a.ArtifactType == ArtifactType.UserStory).ToList();
            var dataEntities = Incoming(fr.Id, RelationshipType.DerivedFrom)
                .Select(r => byId[r.SourceArtifactId]).Where(a => a.ArtifactType == ArtifactType.DataEntity);
            var apiSpecs = Incoming(fr.Id, RelationshipType.DerivedFrom)
                .Select(r => byId[r.SourceArtifactId]).Where(a => a.ArtifactType == ArtifactType.ApiSpecification);
            var tasks = Incoming(fr.Id, RelationshipType.Implements).Select(r => byId[r.SourceArtifactId]);
            var testCases = Outgoing(fr.Id, RelationshipType.TestedBy).Select(r => byId[r.TargetArtifactId]);

            // DesignArtifact isn't linked per-FR (§16 is source-level architecture, not
            // requirement-level) — fall back to sharing the same RequirementSourceId.
            var designArtifacts = artifacts.Where(a =>
                a.ArtifactType == ArtifactType.DesignArtifact && a.RequirementSourceId == fr.RequirementSourceId);

            var acceptanceCriteria = userStories
                .SelectMany(story => Incoming(story.Id, RelationshipType.DerivedFrom))
                .Select(r => byId[r.SourceArtifactId])
                .Where(a => a.ArtifactType == ArtifactType.AcceptanceCriterion);

            rows.Add(new TraceabilityRow(
                fr.Id, fr.Code, fr.Title,
                businessRules.Select(ToRef).ToList(),
                userStories.Select(ToRef).ToList(),
                acceptanceCriteria.Select(ToRef).ToList(),
                designArtifacts.Select(ToRef).ToList(),
                dataEntities.Select(ToRef).ToList(),
                apiSpecs.Select(ToRef).ToList(),
                tasks.Select(ToRef).ToList(),
                testCases.Select(ToRef).ToList()));
        }

        return rows;
    }
}
