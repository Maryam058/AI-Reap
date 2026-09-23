using System.Text.Json;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Artifacts;

public class ArtifactService : IArtifactService
{
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;

    public ArtifactService(IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
    }

    public async Task<IReadOnlyList<ArtifactResponse>> GetForProjectAsync(
        Guid projectId, ArtifactType? type, Guid? requirementSourceId, CancellationToken cancellationToken = default)
    {
        var query = _db.Artifacts.Where(a => a.ProjectId == projectId);

        if (type is not null)
        {
            query = query.Where(a => a.ArtifactType == type);
        }

        if (requirementSourceId is not null)
        {
            query = query.Where(a => a.RequirementSourceId == requirementSourceId);
        }

        var artifacts = await query.OrderBy(a => a.Code).ToListAsync(cancellationToken);
        return artifacts.Select(ToResponse).ToList();
    }

    public async Task<ArtifactResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var artifact = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (artifact is null)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(artifact.ProjectId, cancellationToken);
        return ToResponse(artifact);
    }

    public async Task<ArtifactResponse?> UpdateAsync(Guid id, UpdateArtifactRequest request, CancellationToken cancellationToken = default)
    {
        var artifact = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (artifact is null)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(artifact.ProjectId, cancellationToken);

        if (request.Title is not null)
        {
            artifact.Title = request.Title;
        }

        if (request.Priority is not null)
        {
            artifact.Priority = request.Priority;
        }

        if (request.Data is not null)
        {
            artifact.DataJson = request.Data.Value.GetRawText();
        }

        await SaveNewVersionAsync(artifact, request.Reason, ArtifactOrigin.Human, cancellationToken);
        return ToResponse(artifact);
    }

    public async Task<ArtifactResponse?> UpdateStatusAsync(Guid id, UpdateArtifactStatusRequest request, CancellationToken cancellationToken = default)
    {
        var artifact = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (artifact is null)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(artifact.ProjectId, cancellationToken);

        artifact.Status = request.Status;
        artifact.UpdatedByUserId = _currentUser.UserId;
        artifact.UpdatedAt = DateTime.UtcNow;

        // §24 — Approved/Rejected are review decisions; other transitions (UnderReview,
        // Implemented, Verified) are workflow progress without a formal review record.
        if (request.Status is ArtifactStatus.Approved or ArtifactStatus.Rejected)
        {
            _db.ArtifactReviews.Add(new ArtifactReview
            {
                Id = Guid.NewGuid(),
                ArtifactId = artifact.Id,
                ReviewerUserId = _currentUser.UserId,
                Decision = request.Status == ArtifactStatus.Approved ? ReviewDecision.Approved : ReviewDecision.Rejected,
                Comment = request.Comment,
                ReviewedAt = DateTime.UtcNow
            });

            // §27 — link the human decision back to the AI execution(s) that produced this
            // artifact (GenerationSupport.AddExecutions logs one execution per produced
            // artifact, so this is normally exactly one row).
            var producingExecutions = await _db.AIExecutions
                .Where(e => e.ProducedArtifactId == artifact.Id)
                .ToListAsync(cancellationToken);
            foreach (var execution in producingExecutions)
            {
                execution.Accepted = request.Status == ArtifactStatus.Approved;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        // §8 — approving an answered clarification question is the human-acceptance signal
        // that closes it out: Answered -> Resolved.
        if (request.Status == ArtifactStatus.Approved && artifact.ArtifactType == ArtifactType.ClarificationQuestion)
        {
            await ResolveClarificationIfAnsweredAsync(artifact, cancellationToken);
        }

        // Epic 3.4/REAP-074: Implemented/Verified aren't reviewer decisions - they're derived
        // automatically from real downstream state (linked tasks/test cases being approved),
        // the only "completion signal" this platform actually has (it doesn't run code or
        // tests itself). Only approving a Task or TestCase can trigger a promotion; there's no
        // demotion if a task/test is later un-approved - that would need a real state machine
        // for artifacts, which doesn't exist today, so it's left as a known limitation rather
        // than half-built.
        if (request.Status == ArtifactStatus.Approved && artifact.ArtifactType is ArtifactType.ImplementationTask or ArtifactType.TestCase)
        {
            await PromoteLinkedFunctionalRequirementsAsync(artifact, cancellationToken);
        }

        return ToResponse(artifact);
    }

    private async Task PromoteLinkedFunctionalRequirementsAsync(Artifact triggeringArtifact, CancellationToken cancellationToken)
    {
        var linkedFrIds = triggeringArtifact.ArtifactType == ArtifactType.ImplementationTask
            ? await _db.ArtifactRelationships
                .Where(r => r.SourceArtifactId == triggeringArtifact.Id && r.RelationshipType == RelationshipType.Implements)
                .Select(r => r.TargetArtifactId)
                .ToListAsync(cancellationToken)
            : await _db.ArtifactRelationships
                .Where(r => r.TargetArtifactId == triggeringArtifact.Id && r.RelationshipType == RelationshipType.TestedBy)
                .Select(r => r.SourceArtifactId)
                .ToListAsync(cancellationToken);

        if (linkedFrIds.Count == 0)
        {
            return;
        }

        var functionalRequirements = await _db.Artifacts
            .Where(a => linkedFrIds.Contains(a.Id) && a.ArtifactType == ArtifactType.FunctionalRequirement)
            .ToListAsync(cancellationToken);

        var changed = false;
        foreach (var fr in functionalRequirements)
        {
            if (fr.Status == ArtifactStatus.Approved)
            {
                var taskIds = await _db.ArtifactRelationships
                    .Where(r => r.TargetArtifactId == fr.Id && r.RelationshipType == RelationshipType.Implements)
                    .Select(r => r.SourceArtifactId)
                    .ToListAsync(cancellationToken);
                var tasks = await _db.Artifacts.Where(a => taskIds.Contains(a.Id)).ToListAsync(cancellationToken);

                if (tasks.Count > 0 && tasks.All(t => t.Status == ArtifactStatus.Approved))
                {
                    fr.Status = ArtifactStatus.Implemented;
                    fr.UpdatedAt = DateTime.UtcNow;
                    changed = true;
                }
            }

            if (fr.Status == ArtifactStatus.Implemented)
            {
                var testCaseIds = await _db.ArtifactRelationships
                    .Where(r => r.SourceArtifactId == fr.Id && r.RelationshipType == RelationshipType.TestedBy)
                    .Select(r => r.TargetArtifactId)
                    .ToListAsync(cancellationToken);
                var testCases = await _db.Artifacts.Where(a => testCaseIds.Contains(a.Id)).ToListAsync(cancellationToken);

                if (testCases.Count > 0 && testCases.All(t => t.Status == ArtifactStatus.Approved))
                {
                    fr.Status = ArtifactStatus.Verified;
                    fr.UpdatedAt = DateTime.UtcNow;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    // §8 — only Answered transitions to Resolved. NotApplicable is already a closed/terminal
    // state, and an Open question (never answered) shouldn't become Resolved just because the
    // artifact itself was approved. No reversion path if the artifact is later rejected - same
    // pattern as the FR auto-promotion above having no demotion (known limitation, documented
    // not hidden, rather than half-built).
    private async Task ResolveClarificationIfAnsweredAsync(Artifact artifact, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<ClarificationQuestionPayload>(
            artifact.DataJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ClarificationQuestionPayload();

        if (payload.ClarificationStatus != "Answered")
        {
            return;
        }

        payload.ClarificationStatus = "Resolved";
        artifact.DataJson = JsonSerializer.Serialize(payload);
        await SaveNewVersionAsync(artifact, "Clarification approved as resolved", ArtifactOrigin.Human, cancellationToken);
    }

    public async Task<ArtifactResponse?> AnswerClarificationAsync(Guid id, ClarificationAnswerRequest request, CancellationToken cancellationToken = default)
    {
        var artifact = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (artifact is null || artifact.ArtifactType != ArtifactType.ClarificationQuestion)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(artifact.ProjectId, cancellationToken);

        var payload = JsonSerializer.Deserialize<ClarificationQuestionPayload>(
            artifact.DataJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ClarificationQuestionPayload();

        payload.Answer = request.Answer;
        payload.ClarificationStatus = request.NotApplicable ? "NotApplicable" : "Answered";
        artifact.DataJson = JsonSerializer.Serialize(payload);

        await SaveNewVersionAsync(artifact, "Clarification answered", ArtifactOrigin.Human, cancellationToken);
        return ToResponse(artifact);
    }

    public async Task<IReadOnlyList<ArtifactVersionResponse>> GetVersionsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureAccessToArtifactAsync(id, cancellationToken);

        var versions = await _db.ArtifactVersions
            .Where(v => v.ArtifactId == id)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(cancellationToken);

        return versions.Select(v => new ArtifactVersionResponse(
            v.VersionNumber,
            ArtifactJson.ToCamelCaseElement(v.DataSnapshotJson),
            v.ChangedByUserId,
            v.ChangedAt,
            v.Reason,
            v.Origin)).ToList();
    }

    public async Task<IReadOnlyList<ArtifactRelationshipResponse>> GetRelationshipsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureAccessToArtifactAsync(id, cancellationToken);

        var outgoing = await _db.ArtifactRelationships
            .Where(r => r.SourceArtifactId == id)
            .Join(_db.Artifacts, r => r.TargetArtifactId, a => a.Id, (r, a) => new { r.RelationshipType, Related = a, IsOutgoing = true })
            .ToListAsync(cancellationToken);

        var incoming = await _db.ArtifactRelationships
            .Where(r => r.TargetArtifactId == id)
            .Join(_db.Artifacts, r => r.SourceArtifactId, a => a.Id, (r, a) => new { r.RelationshipType, Related = a, IsOutgoing = false })
            .ToListAsync(cancellationToken);

        return outgoing.Concat(incoming)
            .Select(x => new ArtifactRelationshipResponse(
                x.Related.Id, x.Related.Code, x.Related.Title, x.Related.ArtifactType, x.RelationshipType, x.IsOutgoing))
            .ToList();
    }

    // GetVersionsAsync/GetRelationshipsAsync don't otherwise load the artifact, only its
    // sub-records by ArtifactId - resolve just the ProjectId to check membership, and skip the
    // check for an unknown id (that already existing behavior - an empty list, not 404 - is
    // preserved rather than invented here).
    private async Task EnsureAccessToArtifactAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        var projectId = await _db.Artifacts.Where(a => a.Id == artifactId).Select(a => (Guid?)a.ProjectId).FirstOrDefaultAsync(cancellationToken);
        if (projectId is not null)
        {
            await _projectAccess.EnsureMemberAsync(projectId.Value, cancellationToken);
        }
    }

    private async Task SaveNewVersionAsync(Artifact artifact, string? reason, ArtifactOrigin changeOrigin, CancellationToken cancellationToken)
    {
        artifact.CurrentVersion += 1;
        artifact.UpdatedByUserId = _currentUser.UserId;
        artifact.UpdatedAt = DateTime.UtcNow;

        _db.ArtifactVersions.Add(new ArtifactVersion
        {
            Id = Guid.NewGuid(),
            ArtifactId = artifact.Id,
            VersionNumber = artifact.CurrentVersion,
            DataSnapshotJson = artifact.DataJson,
            ChangedByUserId = _currentUser.UserId,
            ChangedAt = DateTime.UtcNow,
            Reason = reason,
            Origin = changeOrigin
        });

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static ArtifactResponse ToResponse(Artifact artifact) => ArtifactResponseMapper.ToResponse(artifact);
}
