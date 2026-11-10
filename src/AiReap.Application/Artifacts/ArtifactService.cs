using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai.Pipeline;
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
        var query = _db.Artifacts.VisibleTo(_currentUser).Where(a => a.ProjectId == projectId);

        if (type is not null)
        {
            query = query.Where(a => a.ArtifactType == type);
        }

        if (requirementSourceId is not null)
        {
            query = query.Where(a => a.RequirementSourceId == requirementSourceId);
        }

        // A clarification question without verified source evidence is never listed (ClarificationVisibility).
        var artifacts = await ClarificationVisibility.FilterAsync(_db, await query.OrderBy(a => a.Code).ToListAsync(cancellationToken), cancellationToken);

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
        if (!_currentUser.CanSee(artifact)
            || artifact.ArtifactType == ArtifactType.ClarificationQuestion && (await ClarificationVisibility.FilterAsync(_db, [artifact], cancellationToken)).Count == 0)
        {
            return null;
        }

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

        // G3 D1/P3 — an FR/NFR's data must be an object, and a derived/assumed origin needs a note.
        if (request.Data is { } requirementData && RequirementEvidenceFields.Applies(artifact.ArtifactType))
        {
            RequirementOriginEdit.Validate(artifact.ArtifactType, artifact.DataJson, requirementData);
        }

        string? newData;
        bool dataChanged;
        if (request.Data is { } data && artifact.ArtifactType == ArtifactType.NonFunctionalRequirement)
        {
            // REAP-045 — an edit never decides the NFR's confirmation; the client's value is ignored.
            var sanitized = NfrAssumption.SanitizeEdit(artifact.DataJson, data);

            // P0-2 — nor its evidence results (RequirementEvidenceFields).
            if (RequirementEvidenceFields.Involved(artifact.DataJson, sanitized))
            {
                sanitized = RequirementEvidenceFields.SanitizeEdit(artifact.DataJson, sanitized, artifact.ArtifactType);
            }

            newData = sanitized.ToJsonString();
            dataChanged = !JsonNode.DeepEquals(sanitized, JsonNode.Parse(artifact.DataJson));
        }
        else if (request.Data is { ValueKind: JsonValueKind.Object } frData
                 && artifact.ArtifactType == ArtifactType.FunctionalRequirement
                 && JsonNode.Parse(frData.GetRawText()) is JsonObject edited
                 && RequirementEvidenceFields.Involved(artifact.DataJson, edited))
        {
            // P0-2 — an FR's evidence and its results are server-owned; anything else is stored as sent.
            var sanitized = RequirementEvidenceFields.SanitizeEdit(artifact.DataJson, edited, artifact.ArtifactType);
            newData = sanitized.ToJsonString();
            dataChanged = !JsonNode.DeepEquals(sanitized, JsonNode.Parse(artifact.DataJson));
        }
        else if (request.Data is { ValueKind: JsonValueKind.Object } cqData
                 && artifact.ArtifactType == ArtifactType.ClarificationQuestion
                 && JsonNode.Parse(cqData.GetRawText()) is JsonObject editedQuestion)
        {
            // A question's source references are server-owned (ClarificationSourceReferences).
            var sanitized = ClarificationSourceReferences.KeepOnEdit(artifact.DataJson, editedQuestion);
            newData = sanitized.ToJsonString();
            dataChanged = !JsonNode.DeepEquals(sanitized, JsonNode.Parse(artifact.DataJson));
        }
        else
        {
            newData = request.Data?.GetRawText();
            dataChanged = newData is not null && newData != artifact.DataJson;
        }

        var changed = (request.Title is not null && request.Title != artifact.Title)
            || (request.Priority is not null && request.Priority != artifact.Priority)
            || dataChanged;

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

        if (dataChanged)
        {
            artifact.DataJson = newData!;
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
    // criteria, APIs, entities, tasks, tests) - flagged for review, never modified. The change,
    // its new version and its notices are written by one save: all of them or none.
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

        AddNewVersion(artifact, reason, ArtifactOrigin.Human);
        if (wasApproved)
        {
            await _impactAnalysis.StageNoticesForChangeAsync(artifact, cancellationToken);
        }

        await ArtifactConcurrency.SaveAsync(_db, cancellationToken);
    }

    public async Task<ArtifactResponse?> UpdateStatusAsync(Guid id, UpdateArtifactStatusRequest request, CancellationToken cancellationToken = default)
    {
        var artifact = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (artifact is null)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(artifact.ProjectId, cancellationToken);

        // S1 - a decision is about the version the reviewer saw. Checked before anything changes.
        if (request.Status is ArtifactStatus.Approved or ArtifactStatus.Rejected
            && request.Version is { } version && version != artifact.CurrentVersion)
        {
            throw new ConflictException(
                $"v{version} is not the current version of {artifact.Code} (v{artifact.CurrentVersion}). {ArtifactConcurrency.ChangedMessage}");
        }

        // Throws InvalidArtifactStatusTransitionException (-> 409) for anything off the state machine,
        // including Implemented/Verified, which only the system derives.
        ArtifactStatusTransitions.EnsureHumanTransition(artifact.Status, request.Status);

        // §24 — a rejection must say why, so the author knows what to rework (mirrors the UI rule).
        if (request.Status == ArtifactStatus.Rejected && string.IsNullOrWhiteSpace(request.Comment))
        {
            throw new RequestValidationException("A reason is required to reject an artifact.");
        }

        // G3 — the same evidence and NFR-target rule as the Design and Testing gates. This request has
        // no override field (Comment is not one), so the refusal is final until the cause is resolved.
        // Checked before anything changes, so a refusal touches no data.
        if (request.Status == ArtifactStatus.Approved && RequirementApprovalEvidence.Applies(artifact.ArtifactType))
        {
            var evidence = RequirementApprovalEvidence.Assess(artifact.ArtifactType, artifact.DataJson);
            if (evidence.Blocks)
            {
                throw new ConflictException(RequirementApprovalEvidence.ApprovalRefusal(artifact.Code, evidence));
            }

            // G3 P2 — separation of duties, whatever roles the caller holds: the requirement's author
            // (who created or generated it) may not approve it. Later editors are not its author.
            if (string.IsNullOrWhiteSpace(artifact.CreatedByUserId))
            {
                throw new ConflictException($"{artifact.Code} has no recorded author, so it cannot be shown that its approver is someone else; it cannot be approved.");
            }

            if (artifact.CreatedByUserId == _currentUser.UserId)
            {
                throw new UnauthorizedAccessException($"You cannot approve {artifact.Code}: you are its author. Another Business Analyst, Reviewer or Administrator must approve it.");
            }
        }

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
                ReviewerRole = ActingRole(Roles.Reviewer, Roles.BusinessAnalyst, Roles.Administrator),
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

        // §8 — approving an answered clarification question is the human-acceptance signal
        // that closes it out: Answered -> Resolved. Saved with the approval (S1): a lost race
        // writes neither.
        if (request.Status == ArtifactStatus.Approved && artifact.ArtifactType == ArtifactType.ClarificationQuestion)
        {
            ResolveClarificationIfAnswered(artifact);
        }

        await ArtifactConcurrency.SaveAsync(_db, cancellationToken);

        // Implemented/Verified are no longer derived from approving a task or test case - approval
        // only accepts the plan. They follow recorded work instead (ArtifactWorkService).

        return ToResponse(artifact);
    }

    // §8 — only Answered transitions to Resolved. NotApplicable is already a closed/terminal
    // state, and an Open question (never answered) shouldn't become Resolved just because the
    // artifact itself was approved. No reversion path if the artifact is later rejected - same
    // pattern as the FR auto-promotion above having no demotion (known limitation, documented
    // not hidden, rather than half-built).
    private void ResolveClarificationIfAnswered(Artifact artifact)
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
        AddNewVersion(artifact, "Clarification approved as resolved", ArtifactOrigin.Human);
        artifact.ApprovedVersion = artifact.CurrentVersion;
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

    // REAP-045 — a Business Analyst or Reviewer confirms an NFR's proposed target. This is
    // requirement validation, not approval: the artifact keeps its status and ApprovedVersion (the
    // version a reviewer actually approved), no impact notices are raised, and the only payload
    // change is AssumptionStatus = confirmed plus who confirmed it and when. It is recorded as a new
    // human version, like every other change to an artifact's data (§23).
    public async Task<ArtifactResponse?> ConfirmNfrTargetAsync(Guid id, ConfirmNfrTargetRequest request, CancellationToken cancellationToken = default)
    {
        var artifact = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (artifact is null)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(artifact.ProjectId, cancellationToken);
        if (!_currentUser.CanSee(artifact))
        {
            return null;
        }

        // Administrator is deliberately not a confirmer: this is a requirements-validation duty.
        if (!_currentUser.IsInRole(Roles.BusinessAnalyst) && !_currentUser.IsInRole(Roles.Reviewer))
        {
            throw new UnauthorizedAccessException("Only a Business Analyst or Reviewer can confirm an NFR target.");
        }

        if (artifact.ArtifactType != ArtifactType.NonFunctionalRequirement)
        {
            throw new RequestValidationException("Only a non-functional requirement has a target to confirm.");
        }

        // Separation of duties, whatever roles the caller holds: neither the NFR's author nor the
        // author of its current version may confirm it.
        var currentVersionAuthor = await _db.ArtifactVersions
            .Where(v => v.ArtifactId == artifact.Id && v.VersionNumber == artifact.CurrentVersion)
            .Select(v => v.ChangedByUserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (artifact.CreatedByUserId == _currentUser.UserId || currentVersionAuthor == _currentUser.UserId)
        {
            throw new UnauthorizedAccessException("You cannot confirm the target of an NFR you authored; another Business Analyst or Reviewer must confirm it.");
        }

        if (request.Version is not { } version)
        {
            throw new RequestValidationException("The version being confirmed is required.");
        }

        if (version != artifact.CurrentVersion)
        {
            throw new ConflictException($"v{version} is not the current version of this NFR (v{artifact.CurrentVersion}). Review the current version before confirming it.");
        }

        if (artifact.Status == ArtifactStatus.Rejected)
        {
            throw new ConflictException("A rejected NFR cannot be confirmed; it must be reworked first.");
        }

        if (NfrAssumption.TargetOf(artifact.DataJson) is null)
        {
            throw new RequestValidationException("This NFR has no target value to confirm.");
        }

        if (NfrAssumption.IsConfirmed(artifact.DataJson))
        {
            throw new ConflictException("This NFR's target is already confirmed.");
        }

        artifact.DataJson = NfrAssumption.Confirm(artifact.DataJson, _currentUser.UserId, DateTime.UtcNow);
        await SaveNewVersionAsync(artifact, "NFR target confirmed", ArtifactOrigin.Human, cancellationToken);
        return ToResponse(artifact);
    }

    public async Task<IReadOnlyList<ArtifactVersionResponse>> GetVersionsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!await EnsureAccessToArtifactAsync(id, cancellationToken))
        {
            return [];
        }

        var versions = await _db.ArtifactVersions
            .Where(v => v.ArtifactId == id)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(cancellationToken);

        return versions.Select(v => new ArtifactVersionResponse(
            v.VersionNumber,
            ArtifactJson.ToCamelCaseElement(DataFor(v.DataSnapshotJson)),
            v.ChangedByUserId,
            v.ChangedAt,
            v.Reason,
            v.Origin,
            v.Title,
            v.Priority,
            v.Status)).ToList();
    }

    // §17/§4 — a comment-only review: feedback on the artifact's current version that neither approves
    // nor rejects it and leaves its status alone. Any project member who can see the artifact may add
    // one (a Developer: technical artifacts in any status, requirements once approved -
    // ArtifactVisibility); it is recorded under the role the reviewer acted as, which is how a
    // Developer's design review is told apart from a Reviewer's approval.
    public async Task<ArtifactReviewResponse?> AddCommentReviewAsync(Guid id, string? comment, CancellationToken cancellationToken = default)
    {
        var artifact = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (artifact is null)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(artifact.ProjectId, cancellationToken);
        if (!_currentUser.CanSee(artifact))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(comment))
        {
            throw new RequestValidationException("A comment-only review needs a comment.");
        }

        var review = new ArtifactReview
        {
            Id = Guid.NewGuid(),
            ArtifactId = artifact.Id,
            ReviewerUserId = _currentUser.UserId,
            ReviewerRole = ActingRole(Roles.Developer, Roles.QA, Roles.Reviewer, Roles.BusinessAnalyst, Roles.Administrator),
            Decision = ReviewDecision.CommentOnly,
            Comment = comment.Trim(),
            ReviewedAt = DateTime.UtcNow,
            VersionNumber = artifact.CurrentVersion
        };
        _db.ArtifactReviews.Add(review);
        await _db.SaveChangesAsync(cancellationToken);

        return new ArtifactReviewResponse(review.Id, review.ReviewerUserId, review.Decision, review.Comment, review.ReviewedAt, review.VersionNumber, review.ReviewerRole);
    }

    // The first of the given roles the caller holds, in preference order.
    private string? ActingRole(params string[] preference) => preference.FirstOrDefault(_currentUser.IsInRole);

    public async Task<IReadOnlyList<ArtifactReviewResponse>> GetReviewsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!await EnsureAccessToArtifactAsync(id, cancellationToken))
        {
            return [];
        }

        return await _db.ArtifactReviews
            .Where(r => r.ArtifactId == id)
            .OrderByDescending(r => r.ReviewedAt)
            .Select(r => new ArtifactReviewResponse(r.Id, r.ReviewerUserId, r.Decision, r.Comment, r.ReviewedAt, r.VersionNumber, r.ReviewerRole))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ArtifactRelationshipResponse>> GetRelationshipsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!await EnsureAccessToArtifactAsync(id, cancellationToken))
        {
            return [];
        }

        // The related artifact's code and title are artifact data too, so the same visibility applies.
        var visible = _db.Artifacts.VisibleTo(_currentUser);
        var outgoing = await _db.ArtifactRelationships
            .Where(r => r.SourceArtifactId == id)
            .Join(visible, r => r.TargetArtifactId, a => a.Id, (r, a) => new { r.RelationshipType, Related = a, IsOutgoing = true })
            .ToListAsync(cancellationToken);

        var incoming = await _db.ArtifactRelationships
            .Where(r => r.TargetArtifactId == id)
            .Join(visible, r => r.SourceArtifactId, a => a.Id, (r, a) => new { r.RelationshipType, Related = a, IsOutgoing = false })
            .ToListAsync(cancellationToken);

        return outgoing.Concat(incoming)
            .Select(x => new ArtifactRelationshipResponse(
                x.Related.Id, x.Related.Code, x.Related.Title, x.Related.ArtifactType, x.RelationshipType, x.IsOutgoing))
            .ToList();
    }

    // REAP-046 — "US-B depends on US-A" is stored as US-B -DependsOn-> US-A, the edge impact analysis
    // already walks. A relationship, not content: like DerivedFrom objective links it creates no
    // version and leaves status and ApprovedVersion alone.
    public async Task<ArtifactRelationshipResponse?> AddDependencyAsync(Guid id, AddDependencyRequest request, CancellationToken cancellationToken = default)
    {
        var story = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (story is null)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(story.ProjectId, cancellationToken);
        if (!_currentUser.CanSee(story))
        {
            return null;
        }

        if (story.ArtifactType != ArtifactType.UserStory)
        {
            throw new RequestValidationException("Only a user story can have story dependencies.");
        }

        if (request.DependsOnArtifactId == id)
        {
            throw new RequestValidationException("A user story cannot depend on itself.");
        }

        // A missing, hidden or other-project target is answered the same way, so the endpoint can't
        // be used to probe for artifacts the caller couldn't otherwise see.
        var target = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == request.DependsOnArtifactId, cancellationToken);
        if (target is null || target.ProjectId != story.ProjectId || !_currentUser.CanSee(target))
        {
            throw new RequestValidationException("The dependency must be a user story in the same project.");
        }

        if (target.ArtifactType != ArtifactType.UserStory)
        {
            throw new RequestValidationException("A user story can only depend on another user story.");
        }

        var projectArtifactIds = _db.Artifacts.Where(a => a.ProjectId == story.ProjectId).Select(a => a.Id);
        var edges = await _db.ArtifactRelationships
            .Where(r => r.RelationshipType == RelationshipType.DependsOn && projectArtifactIds.Contains(r.SourceArtifactId))
            .Select(r => new { r.SourceArtifactId, r.TargetArtifactId })
            .ToListAsync(cancellationToken);

        if (edges.Any(e => e.SourceArtifactId == id && e.TargetArtifactId == target.Id))
        {
            throw new ConflictException($"{story.Code} already depends on {target.Code}.");
        }

        // The new edge closes a cycle if the target already (transitively) depends on this story.
        var dependsOn = edges.ToLookup(e => e.SourceArtifactId, e => e.TargetArtifactId);
        var seen = new HashSet<Guid> { target.Id };
        var pending = new Queue<Guid>([target.Id]);
        while (pending.Count > 0)
        {
            foreach (var next in dependsOn[pending.Dequeue()])
            {
                if (next == id)
                {
                    throw new ConflictException($"{target.Code} already depends on {story.Code}, directly or indirectly; the dependency would form a cycle.");
                }

                if (seen.Add(next))
                {
                    pending.Enqueue(next);
                }
            }
        }

        _db.ArtifactRelationships.Add(new ArtifactRelationship
        {
            Id = Guid.NewGuid(),
            SourceArtifactId = story.Id,
            TargetArtifactId = target.Id,
            RelationshipType = RelationshipType.DependsOn,
            CreatedAt = DateTime.UtcNow
        });

        // A concurrent request adding the same link passes the check above too; the second insert then
        // hits the relationship's unique index. Answered like the check: 409, nothing saved. Any other
        // database failure propagates unchanged.
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ArtifactPersistence.IsUniqueViolationOn(ex, TraceabilityService.ObjectiveLinkIndex))
        {
            ArtifactPersistence.Undo(_db);
            throw new ConflictException($"{story.Code} already depends on {target.Code}.");
        }

        return new ArtifactRelationshipResponse(target.Id, target.Code, target.Title, target.ArtifactType, RelationshipType.DependsOn, true);
    }

    public async Task<bool> RemoveDependencyAsync(Guid id, Guid targetId, CancellationToken cancellationToken = default)
    {
        var story = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (story is null)
        {
            return false;
        }

        await _projectAccess.EnsureMemberAsync(story.ProjectId, cancellationToken);
        if (!_currentUser.CanSee(story) || story.ArtifactType != ArtifactType.UserStory)
        {
            return false;
        }

        // Only a story-to-story DependsOn link is removed here; no other relationship between the pair.
        var storyIds = _db.Artifacts.Where(a => a.ProjectId == story.ProjectId && a.ArtifactType == ArtifactType.UserStory).Select(a => a.Id);
        var links = await _db.ArtifactRelationships
            .Where(r => r.SourceArtifactId == id && r.TargetArtifactId == targetId && r.RelationshipType == RelationshipType.DependsOn
                        && storyIds.Contains(r.TargetArtifactId))
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
        {
            return false;
        }

        _db.ArtifactRelationships.RemoveRange(links);

        // A concurrent request removed the link first: the DELETE affected no row. Relationships carry
        // no concurrency token and the unique index allows one such link per pair, so that is the only
        // way this save reports a concurrency conflict. Answered as for a link that is already gone
        // (false -> 404); any other database failure propagates unchanged.
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            ArtifactPersistence.Undo(_db);
            return false;
        }

        return true;
    }

    // GetVersionsAsync/GetRelationshipsAsync don't otherwise load the artifact, only its
    // sub-records by ArtifactId - resolve just the ProjectId, type and status to check membership and
    // visibility. An unknown id keeps its existing behavior (an empty list, not 404), and an
    // artifact the caller may not see (ArtifactVisibility) is answered the same way.
    private async Task<bool> EnsureAccessToArtifactAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        var artifact = await _db.Artifacts.Where(a => a.Id == artifactId)
            .Select(a => new { a.ProjectId, a.ArtifactType, a.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (artifact is null)
        {
            return false;
        }

        await _projectAccess.EnsureMemberAsync(artifact.ProjectId, cancellationToken);
        return _currentUser.CanSee(artifact.ArtifactType, artifact.Status);
    }

    // S1 - the version number is the artifact's concurrency token: two changes made from the same
    // version cannot both be saved, so they cannot both mint version N+1.
    private async Task SaveNewVersionAsync(Artifact artifact, string? reason, ArtifactOrigin changeOrigin, CancellationToken cancellationToken)
    {
        AddNewVersion(artifact, reason, changeOrigin);
        await ArtifactConcurrency.SaveAsync(_db, cancellationToken);
    }

    private void AddNewVersion(Artifact artifact, string? reason, ArtifactOrigin changeOrigin)
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
    }

    // S2/P0-2 — a caller who may not read raw sources gets requirement evidence without its quote text,
    // and clarification questions without their source references.
    private ArtifactResponse ToResponse(Artifact artifact)
    {
        var response = ArtifactResponseMapper.ToResponse(artifact);
        return _currentUser.CanReadRawSources()
               || !RequirementEvidenceFields.Applies(artifact.ArtifactType) && artifact.ArtifactType != ArtifactType.ClarificationQuestion
            ? response
            : response with { Data = ArtifactJson.ToCamelCaseElement(RequirementEvidenceFields.RedactForRestrictedCaller(artifact.DataJson)) };
    }

    private string DataFor(string dataJson) =>
        _currentUser.CanReadRawSources() ? dataJson : RequirementEvidenceFields.RedactForRestrictedCaller(dataJson);
}
