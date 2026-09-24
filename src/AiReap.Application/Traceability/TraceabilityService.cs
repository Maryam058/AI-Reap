using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Traceability;

public class TraceabilityService : ITraceabilityService
{
    private readonly IAiReapDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly ICurrentUser _currentUser;

    public TraceabilityService(IAiReapDbContext db, IProjectAccessService projectAccess, ICurrentUser currentUser)
    {
        _db = db;
        _projectAccess = projectAccess;
        _currentUser = currentUser;
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
            var businessObjectives = Outgoing(fr.Id, RelationshipType.DerivedFrom)
                .Select(r => byId[r.TargetArtifactId]).Where(a => a.ArtifactType == ArtifactType.BusinessObjective);
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
                businessObjectives.OrderBy(a => a.Code).Select(ToRef).ToList(),
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

    public async Task<IReadOnlyList<BusinessObjectiveResponse>> GetBusinessObjectivesAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var objectives = await _db.Artifacts
            .Where(a => a.ProjectId == projectId && a.ArtifactType == ArtifactType.BusinessObjective)
            .OrderBy(a => a.Code)
            .ToListAsync(cancellationToken);
        var ids = objectives.Select(o => o.Id).ToList();

        var links = await _db.ArtifactRelationships
            .Where(r => r.RelationshipType == RelationshipType.DerivedFrom && ids.Contains(r.TargetArtifactId))
            .Join(_db.Artifacts, r => r.SourceArtifactId, a => a.Id, (r, a) => new { r.TargetArtifactId, Requirement = a })
            .ToListAsync(cancellationToken);

        return objectives.Select(o => new BusinessObjectiveResponse(o.Id, o.Code, o.Title,
                links.Where(l => l.TargetArtifactId == o.Id).Select(l => new TraceRef(l.Requirement.Id, l.Requirement.Code, l.Requirement.Title))
                    .OrderBy(t => t.Code).ToList()))
            .ToList();
    }

    // Replaces the requirement's objective links (FR/NFR -DerivedFrom-> BO). Traceability metadata,
    // not requirement content, so no new version or re-review is triggered.
    public async Task<IReadOnlyList<TraceRef>?> SetBusinessObjectivesAsync(
        Guid requirementId, IReadOnlyList<Guid> objectiveIds, CancellationToken cancellationToken = default)
    {
        var requirement = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == requirementId, cancellationToken);
        if (requirement is null || requirement.ArtifactType is not (ArtifactType.FunctionalRequirement or ArtifactType.NonFunctionalRequirement))
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(requirement.ProjectId, cancellationToken);

        var wanted = objectiveIds.Distinct().ToList();
        var objectives = await _db.Artifacts
            .Where(a => wanted.Contains(a.Id) && a.ProjectId == requirement.ProjectId && a.ArtifactType == ArtifactType.BusinessObjective)
            .ToListAsync(cancellationToken);
        if (objectives.Count != wanted.Count)
        {
            throw new RequestValidationException("Every id must be a business objective in the same project.");
        }

        var objectiveTypeIds = _db.Artifacts.Where(a => a.ArtifactType == ArtifactType.BusinessObjective).Select(a => a.Id);
        var current = await _db.ArtifactRelationships
            .Where(r => r.SourceArtifactId == requirementId && r.RelationshipType == RelationshipType.DerivedFrom
                        && objectiveTypeIds.Contains(r.TargetArtifactId))
            .ToListAsync(cancellationToken);

        foreach (var stale in current.Where(r => !wanted.Contains(r.TargetArtifactId)))
        {
            _db.ArtifactRelationships.Remove(stale);
        }

        var now = DateTime.UtcNow;
        foreach (var objective in objectives.Where(o => current.All(r => r.TargetArtifactId != o.Id)))
        {
            _db.ArtifactRelationships.Add(new ArtifactRelationship
            {
                Id = Guid.NewGuid(),
                SourceArtifactId = requirementId,
                TargetArtifactId = objective.Id,
                RelationshipType = RelationshipType.DerivedFrom,
                CreatedAt = now
            });
        }

        requirement.UpdatedAt = now;
        requirement.UpdatedByUserId = _currentUser.UserId;
        await _db.SaveChangesAsync(cancellationToken);

        return objectives.OrderBy(o => o.Code).Select(o => new TraceRef(o.Id, o.Code, o.Title)).ToList();
    }
}
