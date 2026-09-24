using System.Text.Json;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Application.Traceability;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Artifacts;

public class ArtifactService : IArtifactService
{
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IImpactAnalysisService _impactAnalysis;

    public ArtifactService(IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess, IImpactAnalysisService impactAnalysis)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _impactAnalysis = impactAnalysis;
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

        var openNotices = await _db.ArtifactImpactNotices
            .Where(n => n.ProjectId == projectId && n.AcknowledgedAt == null)
            .GroupBy(n => n.AffectedArtifactId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        return artifacts
            .Select(a => ToResponse(a) with { OpenImpactNoticeCount = openNotices.GetValueOrDefault(a.Id) })
            .ToList();
    }

    public async Task<ArtifactResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var artifact = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (artifact is null)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(artifact.ProjectId, cancellationToken);
        var openNotices = await _db.ArtifactImpactNotices.CountAsync(n => n.AffectedArtifactId == id && n.AcknowledgedAt == null, cancellationToken);
        return ToResponse(artifact) with { OpenImpactNoticeCount = openNotices };
    }

    public async Task<ArtifactResponse?> UpdateAsync(Guid id, UpdateArtifactRequest request, CancellationToken cancellationToken = default)
    {
        var artifact = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (artifact is null)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(artifact.ProjectId, cancellationToken);

        if (request.Title is not null && string.IsNullOrWhiteSpace(request.Title))
        {
            throw new RequestValidationException("Title cannot be empty.");
        }

        var newData = request.Data?.GetRawText();
        var changed = (request.Title is not null && request.Title != artifact.Title)
            || (request.Priority is not null && request.Priority != artifact.Priority)
            || (newData is not null && newData != artifact.DataJson);

        // Nothing actually differs - don't mint an empty version, and don't drop an approval over it.
        if (!changed)
        {
            return ToResponse(artifact);
        }

        if (request.Title is not null)
        {
            artifact.Title = request.Title.Trim();
        }

        if (request.Priority is not null)
        {
            artifact.Priority = request.Priority;
        }

        if (newData is not null)
        {
            artifact.DataJson = newData;
        }

        await ApplyHumanContentChangeAsync(artifact, request.Reason, cancellationToken);
        return ToResponse(artifact);
    }

    // §22/§24 — approved content is never changed in place under its approval. The edit becomes a
    // new version in UnderReview; the approved version stays intact in ArtifactVersions (with its
    // Approved status snapshot) and Artifact.ApprovedVersion keeps pointing at it until a reviewer
    // approves the new one.
    //
    // Changing approved content also raises §22 impact notices on everything downstream (stories,
    // criteria, APIs, entities, tasks, tests) - flagged for review, never modified.
    private async Task ApplyHumanContentChangeAsync(Artifact artifact, string? reason, CancellationToken cancellationToken)
    {
        var wasApproved = ArtifactStatusTransitions.IsApprovedOrLater(artifact.Status);
        if (wasApproved)
        {
            artifact.Status = ArtifactStatus.UnderReview;
            reason = string.IsNullOrWhiteSpace(reason)
                ? $"Edited after approval of v{artifact.ApprovedVersion ?? artifact.CurrentVersion}; re-review required"
                : $"{reason} (edited after approval of v{artifact.ApprovedVersion ?? artifact.CurrentVersion}; re-review required)";
        }

        await SaveNewVersionAsync(artifact, reason, ArtifactOrigin.Human, cancellationToken);

        if (wasApproved)
        {
            await _impactAnalysis.RaiseNoticesForChangeAsync(artifact, cancellationToken);
        }
    }

    public async Task<ArtifactResponse?> UpdateStatusAsync(Guid id, UpdateArtifactStatusRequest request, CancellationToken cancellationToken = default)
    {
        var artifact = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (artifact is null)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(artifact.ProjectId, cancellationToken);

        // Throws InvalidArtifactStatusTransitionException (-> 409) for anything off the state machine,
        // including Implemented/Verified, which only the system derives.
        ArtifactStatusTransitions.EnsureHumanTransition(artifact.Status, request.Status);

        artifact.Status = request.Status;
        artifact.UpdatedByUserId = _currentUser.UserId;
        artifact.UpdatedAt = DateTime.UtcNow;

        if (request.Status == ArtifactStatus.Approved)
        {
            artifact.ApprovedVersion = artifact.CurrentVersion;
        }

        // §24 — Approved/Rejected are review decisions; other transitions (UnderReview, Draft)
        // are workflow progress without a formal review record.
        if (request.Status is ArtifactStatus.Approved or ArtifactStatus.Rejected)
        {
            _db.ArtifactReviews.Add(new ArtifactReview
            {
                Id = Guid.NewGuid(),
                ArtifactId = artifact.Id,
                ReviewerUserId = _currentUser.UserId,
                Decision = request.Status == ArtifactStatus.Approved ? ReviewDecision.Approved : ReviewDecision.Rejected,
                Comment = request.Comment,
                ReviewedAt = DateTime.UtcNow,
                VersionNumber = artifact.CurrentVersion
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
        // tests itself). Only approving a Task or TestCase can trigger a promotion, and only along
        // ArtifactStatusTransitions.CanSystemPromote. There's no automatic demotion if a task/test
        // is later un-approved (known limitation); a reviewer can reopen the FR (-> UnderReview).
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

                if (tasks.Count > 0 && tasks.All(t => t.Status == ArtifactStatus.Approved)
                    && ArtifactStatusTransitions.CanSystemPromote(fr.Status, ArtifactStatus.Implemented))
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

                if (testCases.Count > 0 && testCases.All(t => t.Status == ArtifactStatus.Approved)
                    && ArtifactStatusTransitions.CanSystemPromote(fr.Status, ArtifactStatus.Verified))
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

        // Part of the approval itself (the reviewer approved the answer), so this version keeps the
        // Approved status and becomes the approved version.
        payload.ClarificationStatus = "Resolved";
        artifact.DataJson = JsonSerializer.Serialize(payload);
        await SaveNewVersionAsync(artifact, "Clarification approved as resolved", ArtifactOrigin.Human, cancellationToken);
        artifact.ApprovedVersion = artifact.CurrentVersion;
        await _db.SaveChangesAsync(cancellationToken);
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

        await ApplyHumanContentChangeAsync(artifact, "Clarification answered", cancellationToken);
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
            v.Origin,
            v.Title,
            v.Priority,
            v.Status)).ToList();
    }

    public async Task<IReadOnlyList<ArtifactReviewResponse>> GetReviewsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureAccessToArtifactAsync(id, cancellationToken);

        return await _db.ArtifactReviews
            .Where(r => r.ArtifactId == id)
            .OrderByDescending(r => r.ReviewedAt)
            .Select(r => new ArtifactReviewResponse(r.Id, r.ReviewerUserId, r.Decision, r.Comment, r.ReviewedAt, r.VersionNumber))
            .ToListAsync(cancellationToken);
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
            Title = artifact.Title,
            Priority = artifact.Priority,
            Status = artifact.Status,
            ChangedByUserId = _currentUser.UserId,
            ChangedAt = DateTime.UtcNow,
            Reason = reason,
            Origin = changeOrigin
        });

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static ArtifactResponse ToResponse(Artifact artifact) => ArtifactResponseMapper.ToResponse(artifact);
}
