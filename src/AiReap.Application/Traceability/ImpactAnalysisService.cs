using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Traceability;

public class ImpactAnalysisService : IImpactAnalysisService
{
    private const int MaxHops = 3;

    private readonly IAiReapDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly ICurrentUser _currentUser;

    public ImpactAnalysisService(IAiReapDbContext db, IProjectAccessService projectAccess, ICurrentUser currentUser)
    {
        _db = db;
        _projectAccess = projectAccess;
        _currentUser = currentUser;
    }

    public async Task<ImpactAnalysisResult?> AnalyzeAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == artifactId, cancellationToken);
        if (artifact is null)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(artifact.ProjectId, cancellationToken);

        var found = await WalkDownstreamAsync(artifact, cancellationToken);

        ImpactedArtifact ToImpacted(Impact x) => new(
            x.Artifact.Id, x.Artifact.Code, x.Artifact.Title, x.Artifact.ArtifactType.ToString(), x.Artifact.Status.ToString(),
            x.Edge.ToString(), x.Path);

        var approvedOrLater = found.Where(x => ArtifactStatusTransitions.IsApprovedOrLater(x.Artifact.Status)).Select(ToImpacted).ToList();
        var other = found.Where(x => !ArtifactStatusTransitions.IsApprovedOrLater(x.Artifact.Status)).Select(ToImpacted).ToList();

        return new ImpactAnalysisResult(artifact.Id, artifact.Code, artifact.Title, approvedOrLater, other);
    }

    // §22 — called when approved content changes. Flags every downstream artifact with an open
    // notice (replacing any still-open notice from an earlier change of the same source, so one
    // artifact never accumulates duplicates). Nothing downstream is modified.
    public async Task<int> RaiseNoticesForChangeAsync(Artifact changed, CancellationToken cancellationToken = default)
    {
        var found = await WalkDownstreamAsync(changed, cancellationToken);
        if (found.Count == 0)
        {
            return 0;
        }

        var affectedIds = found.Select(f => f.Artifact.Id).ToList();
        var stillOpen = await _db.ArtifactImpactNotices
            .Where(n => n.SourceArtifactId == changed.Id && n.AcknowledgedAt == null && affectedIds.Contains(n.AffectedArtifactId))
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        foreach (var old in stillOpen)
        {
            // Superseded by the newer change; recorded as acknowledged by the system, not deleted.
            old.AcknowledgedAt = now;
            old.AcknowledgedByUserId = _currentUser.UserId;
            old.AcknowledgementNote = $"Superseded by v{changed.CurrentVersion} of {changed.Code}.";
        }

        foreach (var impact in found)
        {
            _db.ArtifactImpactNotices.Add(new ArtifactImpactNotice
            {
                Id = Guid.NewGuid(),
                ProjectId = changed.ProjectId,
                SourceArtifactId = changed.Id,
                SourceVersion = changed.CurrentVersion,
                AffectedArtifactId = impact.Artifact.Id,
                Path = impact.Path.Length <= 1000 ? impact.Path : impact.Path[..1000],
                CreatedByUserId = _currentUser.UserId,
                CreatedAt = now
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        return found.Count;
    }

    public async Task<IReadOnlyList<ImpactNoticeResponse>> GetNoticesForProjectAsync(Guid projectId, bool includeAcknowledged, CancellationToken cancellationToken = default)
    {
        var query = _db.ArtifactImpactNotices.Where(n => n.ProjectId == projectId);
        if (!includeAcknowledged)
        {
            query = query.Where(n => n.AcknowledgedAt == null);
        }

        return await ToResponsesAsync(query, cancellationToken);
    }

    public async Task<IReadOnlyList<ImpactNoticeResponse>> GetNoticesForArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var projectId = await _db.Artifacts.Where(a => a.Id == artifactId).Select(a => (Guid?)a.ProjectId).FirstOrDefaultAsync(cancellationToken);
        if (projectId is null)
        {
            return [];
        }

        await _projectAccess.EnsureMemberAsync(projectId.Value, cancellationToken);
        return await ToResponsesAsync(_db.ArtifactImpactNotices.Where(n => n.AffectedArtifactId == artifactId), cancellationToken);
    }

    public async Task<ImpactNoticeResponse?> AcknowledgeAsync(Guid noticeId, string? note, CancellationToken cancellationToken = default)
    {
        var notice = await _db.ArtifactImpactNotices.FirstOrDefaultAsync(n => n.Id == noticeId, cancellationToken);
        if (notice is null)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(notice.ProjectId, cancellationToken);

        if (notice.AcknowledgedAt is null)
        {
            notice.AcknowledgedAt = DateTime.UtcNow;
            notice.AcknowledgedByUserId = _currentUser.UserId;
            notice.AcknowledgementNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            await _db.SaveChangesAsync(cancellationToken);
        }

        return (await ToResponsesAsync(_db.ArtifactImpactNotices.Where(n => n.Id == noticeId), cancellationToken)).Single();
    }

    private async Task<IReadOnlyList<ImpactNoticeResponse>> ToResponsesAsync(IQueryable<ArtifactImpactNotice> notices, CancellationToken cancellationToken)
    {
        return await notices
            .OrderByDescending(n => n.CreatedAt)
            .Join(_db.Artifacts, n => n.SourceArtifactId, a => a.Id, (n, source) => new { n, source })
            .Join(_db.Artifacts, x => x.n.AffectedArtifactId, a => a.Id, (x, affected) => new ImpactNoticeResponse(
                x.n.Id, x.n.SourceArtifactId, x.source.Code, x.source.Title, x.n.SourceVersion,
                x.n.AffectedArtifactId, affected.Code, affected.Title, affected.ArtifactType, affected.Status,
                x.n.Path, x.n.CreatedByUserId, x.n.CreatedAt, x.n.AcknowledgedByUserId, x.n.AcknowledgedAt, x.n.AcknowledgementNote))
            .ToListAsync(cancellationToken);
    }

    private sealed record Impact(Artifact Artifact, RelationshipType Edge, string Path);

    // Directed downstream walk (up to MaxHops). An edge points "downstream" when the neighbour was
    // produced from, implements, tests, or is governed by the current artifact:
    //   X <-DerivedFrom- Y, X <-Implements- Y, X <-DependsOn- Y, X <-LinkedRule- Y, X -TestedBy-> Y.
    // Conflict/duplicate pairs are sideways: reported one hop out, never walked through. Upstream
    // artifacts (e.g. the business objective an FR serves) are never reported as "impacted".
    private async Task<List<Impact>> WalkDownstreamAsync(Artifact root, CancellationToken cancellationToken)
    {
        var allArtifacts = await _db.Artifacts.Where(a => a.ProjectId == root.ProjectId).ToListAsync(cancellationToken);
        var byId = allArtifacts.ToDictionary(a => a.Id);
        var ids = byId.Keys.ToList();
        var relationships = await _db.ArtifactRelationships
            .Where(r => ids.Contains(r.SourceArtifactId) || ids.Contains(r.TargetArtifactId))
            .ToListAsync(cancellationToken);

        IEnumerable<(Guid Next, RelationshipType Edge, bool Traverse, string Verb)> Downstream(Guid id)
        {
            foreach (var r in relationships)
            {
                if (r.TargetArtifactId == id && r.RelationshipType is RelationshipType.DerivedFrom or RelationshipType.Implements
                        or RelationshipType.DependsOn or RelationshipType.LinkedRule)
                {
                    yield return (r.SourceArtifactId, r.RelationshipType, true, Verb(r.RelationshipType));
                }
                else if (r.SourceArtifactId == id && r.RelationshipType == RelationshipType.TestedBy)
                {
                    yield return (r.TargetArtifactId, r.RelationshipType, true, "tests");
                }
                else if (r.RelationshipType is RelationshipType.ConflictsWith or RelationshipType.DuplicateOf
                         && (r.SourceArtifactId == id || r.TargetArtifactId == id))
                {
                    yield return (r.SourceArtifactId == id ? r.TargetArtifactId : r.SourceArtifactId, r.RelationshipType, false,
                        r.RelationshipType == RelationshipType.ConflictsWith ? "conflicts with" : "duplicates");
                }
            }
        }

        var visited = new HashSet<Guid> { root.Id };
        var found = new List<Impact>();
        var frontier = new List<(Guid Id, string Path)> { (root.Id, root.Code) };

        for (var hop = 0; hop < MaxHops && frontier.Count > 0; hop++)
        {
            var next = new List<(Guid, string)>();
            foreach (var (currentId, currentPath) in frontier)
            {
                foreach (var (otherId, edge, traverse, verb) in Downstream(currentId))
                {
                    if (!visited.Add(otherId) || !byId.TryGetValue(otherId, out var other))
                    {
                        continue;
                    }

                    var path = $"{other.Code} {verb} {currentPath}";
                    found.Add(new Impact(other, edge, path));
                    if (traverse)
                    {
                        next.Add((otherId, path));
                    }
                }
            }

            frontier = next;
        }

        return found;
    }

    private static string Verb(RelationshipType type) => type switch
    {
        RelationshipType.DerivedFrom => "derived from",
        RelationshipType.Implements => "implements",
        RelationshipType.DependsOn => "depends on",
        RelationshipType.LinkedRule => "is a rule for",
        _ => type.ToString()
    };
}
