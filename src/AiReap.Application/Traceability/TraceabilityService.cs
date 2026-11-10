using AiReap.Application.Artifacts;
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
        var artifacts = await _db.Artifacts.VisibleTo(_currentUser).Where(a => a.ProjectId == projectId).ToListAsync(cancellationToken);
        // Only links between artifacts the caller can see: a link to a hidden one is dropped, not dangling.
        var visibleIds = artifacts.Select(a => a.Id).ToHashSet();
        var relationships = (await _db.ArtifactRelationships
            .Where(r => artifacts.Select(a => a.Id).Contains(r.SourceArtifactId) || artifacts.Select(a => a.Id).Contains(r.TargetArtifactId))
            .ToListAsync(cancellationToken))
            .Where(r => visibleIds.Contains(r.SourceArtifactId) && visibleIds.Contains(r.TargetArtifactId))
            .ToList();

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

    // Upstream link each downstream type must have to appear in the matrix: (relationship, direction,
    // the types it may reach, how the missing link is named).
    private static readonly Dictionary<ArtifactType, (RelationshipType Type, bool Outgoing, ArtifactType[] Upstream, string Missing)> UpstreamLinks = new()
    {
        [ArtifactType.UserStory] = (RelationshipType.DerivedFrom, true, [ArtifactType.FunctionalRequirement, ArtifactType.NonFunctionalRequirement], "requirement"),
        [ArtifactType.AcceptanceCriterion] = (RelationshipType.DerivedFrom, true, [ArtifactType.UserStory], "user story"),
        [ArtifactType.BusinessRule] = (RelationshipType.LinkedRule, true, [ArtifactType.FunctionalRequirement, ArtifactType.NonFunctionalRequirement], "requirement"),
        [ArtifactType.DataEntity] = (RelationshipType.DerivedFrom, true, [ArtifactType.FunctionalRequirement, ArtifactType.NonFunctionalRequirement], "requirement"),
        [ArtifactType.ApiSpecification] = (RelationshipType.DerivedFrom, true, [ArtifactType.FunctionalRequirement, ArtifactType.NonFunctionalRequirement], "requirement"),
        [ArtifactType.ImplementationTask] = (RelationshipType.Implements, true, [ArtifactType.FunctionalRequirement, ArtifactType.NonFunctionalRequirement], "requirement"),
        [ArtifactType.TestCase] = (RelationshipType.TestedBy, false, [ArtifactType.FunctionalRequirement, ArtifactType.NonFunctionalRequirement], "requirement"),
    };

    public async Task<TraceabilityGapsResponse> GetGapsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await _projectAccess.EnsureMemberAsync(projectId, cancellationToken);

        // Links are judged against every artifact of the project, so an artifact traced to a requirement
        // the caller may not see yet is not reported as an orphan; only artifacts the caller can see are listed.
        var types = await _db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId)
            .Select(a => new { a.Id, a.ArtifactType, a.Status })
            .ToDictionaryAsync(a => a.Id, a => (a.ArtifactType, a.Status), cancellationToken);
        var visible = await _db.Artifacts.VisibleTo(_currentUser).AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Status != ArtifactStatus.Rejected)
            .ToListAsync(cancellationToken);
        var ids = types.Keys.ToList();
        var relationships = await _db.ArtifactRelationships.AsNoTracking()
            .Where(r => ids.Contains(r.SourceArtifactId))
            .Select(r => new { r.SourceArtifactId, r.TargetArtifactId, r.RelationshipType })
            .ToListAsync(cancellationToken);

        bool Live(Guid id, ArtifactType[] allowed) =>
            types.TryGetValue(id, out var t) && t.Status != ArtifactStatus.Rejected && allowed.Contains(t.ArtifactType);

        var bySource = relationships.ToLookup(r => r.SourceArtifactId);
        var byTarget = relationships.ToLookup(r => r.TargetArtifactId);

        var orphans = new List<TraceabilityOrphan>();
        foreach (var artifact in visible.OrderBy(a => a.ArtifactType).ThenBy(a => a.Code))
        {
            if (!UpstreamLinks.TryGetValue(artifact.ArtifactType, out var rule))
            {
                continue;
            }

            var linked = rule.Outgoing
                ? bySource[artifact.Id].Any(r => r.RelationshipType == rule.Type && Live(r.TargetArtifactId, rule.Upstream))
                : byTarget[artifact.Id].Any(r => r.RelationshipType == rule.Type && Live(r.SourceArtifactId, rule.Upstream));
            if (!linked)
            {
                orphans.Add(new TraceabilityOrphan(artifact.Id, artifact.Code, artifact.Title, artifact.ArtifactType, artifact.Status, rule.Missing));
            }
        }

        // An NFR nothing downstream refers to: no story, rule, design, task or test traces to it. Its own
        // link up to a business objective, and conflict/duplicate/dependency links, are not downstream work.
        var nfrs = visible.Where(a => a.ArtifactType == ArtifactType.NonFunctionalRequirement).OrderBy(a => a.Code).ToList();
        bool Downstream(RelationshipType type, Guid other) =>
            type is not (RelationshipType.ConflictsWith or RelationshipType.DuplicateOf or RelationshipType.DependsOn)
            && types.TryGetValue(other, out var t) && t.Status != ArtifactStatus.Rejected
            && t.ArtifactType is not (ArtifactType.BusinessObjective or ArtifactType.FunctionalRequirement or ArtifactType.NonFunctionalRequirement);
        var referenced = nfrs
            .Where(n => bySource[n.Id].Any(r => Downstream(r.RelationshipType, r.TargetArtifactId))
                || byTarget[n.Id].Any(r => Downstream(r.RelationshipType, r.SourceArtifactId)))
            .Select(n => n.Id)
            .ToHashSet();
        var untraced = nfrs.Where(n => !referenced.Contains(n.Id)).Select(n => new TraceRef(n.Id, n.Code, n.Title)).ToList();

        var visibleTests = visible.Where(a => a.ArtifactType == ArtifactType.TestCase).ToDictionary(a => a.Id);
        var coverage = nfrs.Select(n => new NfrTestCoverage(
                n.Id, n.Code, n.Title, NfrCategory(n.DataJson),
                bySource[n.Id]
                    .Where(r => r.RelationshipType == RelationshipType.TestedBy && visibleTests.ContainsKey(r.TargetArtifactId))
                    .Select(r => visibleTests[r.TargetArtifactId])
                    .DistinctBy(t => t.Id)
                    .OrderBy(t => t.Code)
                    .Select(t => new TraceRef(t.Id, t.Code, t.Title))
                    .ToList()))
            .ToList();

        return new TraceabilityGapsResponse(orphans, untraced, nfrs.Count) { NonFunctionalTestCoverage = coverage };
    }

    // The way to fix an orphan without regenerating it: a person links it to what it traces to. Only the
    // link its type uses (UpstreamLinks), only to a live artifact of an allowed type in the same project
    // that the caller can see. Nothing about either artifact changes; linking twice is a no-op.
    public async Task<TraceRef?> LinkToRequirementAsync(Guid artifactId, Guid targetArtifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await _db.Artifacts.VisibleTo(_currentUser).FirstOrDefaultAsync(a => a.Id == artifactId, cancellationToken);
        if (artifact is null)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(artifact.ProjectId, cancellationToken);
        if (!UpstreamLinks.TryGetValue(artifact.ArtifactType, out var rule))
        {
            throw new RequestValidationException($"{artifact.Code} is not a kind of artifact that is linked to a requirement.");
        }

        if (artifact.Status == ArtifactStatus.Rejected)
        {
            throw new ConflictException($"{artifact.Code} has been rejected; rework it before linking it.");
        }

        var target = await _db.Artifacts.VisibleTo(_currentUser)
            .FirstOrDefaultAsync(a => a.Id == targetArtifactId && a.ProjectId == artifact.ProjectId, cancellationToken)
            ?? throw new RequestValidationException("The artifact to link to was not found in this project.");
        if (!rule.Upstream.Contains(target.ArtifactType))
        {
            throw new RequestValidationException($"{artifact.Code} can only be linked to a {rule.Missing}; {target.Code} is not one.");
        }

        if (target.Status == ArtifactStatus.Rejected)
        {
            throw new ConflictException($"{target.Code} has been rejected, so nothing is linked to it.");
        }

        var (sourceId, targetId) = rule.Outgoing ? (artifact.Id, target.Id) : (target.Id, artifact.Id);
        var exists = await _db.ArtifactRelationships.AnyAsync(
            r => r.SourceArtifactId == sourceId && r.TargetArtifactId == targetId && r.RelationshipType == rule.Type, cancellationToken);
        if (!exists)
        {
            _db.ArtifactRelationships.Add(new ArtifactRelationship
            {
                Id = Guid.NewGuid(),
                SourceArtifactId = sourceId,
                TargetArtifactId = targetId,
                RelationshipType = rule.Type,
                CreatedAt = DateTime.UtcNow
            });
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // The unique (source, type, target) index: the same link was saved by a concurrent request.
                throw new ConflictException($"{artifact.Code} was linked to {target.Code} by someone else at the same time. Refresh to see it.");
            }
        }

        return new TraceRef(target.Id, target.Code, target.Title);
    }

    private static string? NfrCategory(string dataJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(dataJson);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "category", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }

            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<BusinessObjectiveResponse>> GetBusinessObjectivesAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var visible = _db.Artifacts.VisibleTo(_currentUser);
        var objectives = await visible
            .Where(a => a.ProjectId == projectId && a.ArtifactType == ArtifactType.BusinessObjective)
            .OrderBy(a => a.Code)
            .ToListAsync(cancellationToken);
        var ids = objectives.Select(o => o.Id).ToList();

        var links = await _db.ArtifactRelationships
            .Where(r => r.RelationshipType == RelationshipType.DerivedFrom && ids.Contains(r.TargetArtifactId))
            .Join(visible, r => r.SourceArtifactId, a => a.Id, (r, a) => new { r.TargetArtifactId, Requirement = a })
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

        // S1 - the requirement's update carries its concurrency tokens, so a concurrent change to it
        // (another objective edit, an edit, a decision) fails this save; a concurrent save adding the
        // same link can instead hit the link's unique index. Both mean nothing was saved (409, reload
        // and retry; not retried automatically). Any other database failure propagates as before.
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            ArtifactPersistence.Undo(_db);
            throw new ConflictException(ObjectivesChangedMessage(requirement.Code));
        }
        catch (DbUpdateException ex) when (ArtifactPersistence.IsUniqueViolationOn(ex, ObjectiveLinkIndex))
        {
            ArtifactPersistence.Undo(_db);
            throw new ConflictException(ObjectivesChangedMessage(requirement.Code));
        }

        return objectives.OrderBy(o => o.Code).Select(o => new TraceRef(o.Id, o.Code, o.Title)).ToList();
    }

    // The unique index on ArtifactRelationships (SourceArtifactId, RelationshipType, TargetArtifactId),
    // as InitialCreate named it.
    public const string ObjectiveLinkIndex = "IX_ArtifactRelationships_SourceArtifactId_RelationshipType_TargetArtifactId";

    public static string ObjectivesChangedMessage(string code) =>
        $"{code} or its business objective links were changed by someone else at the same time. Your change was not saved; reload and try again.";
}
