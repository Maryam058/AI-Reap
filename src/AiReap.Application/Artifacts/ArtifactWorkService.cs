using AiReap.Application.Common;
using AiReap.Application.Lifecycle;
using AiReap.Application.Persistence;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Artifacts;

// Implemented/Verified mean the work happened, not that the plan was approved. A Developer marks an
// approved task complete; QA records an approved test case's outcome. Those records - never the
// artifact's content or status - are the evidence, so recording work creates no version and leaves
// approval untouched. A functional requirement is then promoted by the system (only along
// ArtifactStatusTransitions.CanSystemPromote): Approved -> Implemented once every linked task is
// complete, Implemented -> Verified once every linked test case has passed. As before there is no
// automatic demotion; lifecycle evidence reads the records directly, so a reopened task or failed
// test still blocks the stage even while the requirement's status lags. An Administrator may record
// work too, but only Developer/QA records are evidence (LifecycleEvidenceService.LatestOutcomes).
public class ArtifactWorkService : IArtifactWorkService
{
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;

    public ArtifactWorkService(IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
    }

    public async Task<ArtifactWorkRecordResponse> RecordAsync(Guid artifactId, RecordWorkRequest request, CancellationToken cancellationToken = default)
    {
        var artifact = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == artifactId, cancellationToken)
            ?? throw new KeyNotFoundException($"Artifact {artifactId} not found.");
        await _projectAccess.EnsureMemberAsync(artifact.ProjectId, cancellationToken);

        var (ownerRole, allowed, noun) = artifact.ArtifactType switch
        {
            ArtifactType.ImplementationTask => (Roles.Developer, new[] { WorkOutcome.TaskCompleted, WorkOutcome.TaskReopened }, "implementation task"),
            ArtifactType.TestCase => (Roles.QA, new[] { WorkOutcome.TestPassed, WorkOutcome.TestFailed, WorkOutcome.TestBlocked }, "test case"),
            _ => throw new RequestValidationException("Work can only be recorded on implementation tasks and test cases.")
        };

        if (!allowed.Contains(request.Outcome))
        {
            throw new RequestValidationException($"{request.Outcome} is not an outcome for an {noun}. Allowed: {string.Join(", ", allowed)}.");
        }

        var role = _currentUser.IsInRole(ownerRole) ? ownerRole
            : _currentUser.IsInRole(Roles.Administrator) ? Roles.Administrator
            : throw new UnauthorizedAccessException($"Only the {ownerRole} role (or an Administrator) can record work on an {noun}.");

        if (!ArtifactStatusTransitions.IsApprovedOrLater(artifact.Status))
        {
            throw new ConflictException($"{artifact.Code} must be approved before work can be recorded against it.");
        }

        var record = new ArtifactWorkRecord
        {
            Id = Guid.NewGuid(),
            ArtifactId = artifact.Id,
            VersionNumber = artifact.CurrentVersion,
            Outcome = request.Outcome,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            RecordedByUserId = _currentUser.UserId,
            RecordedByRole = role,
            RecordedAt = DateTime.UtcNow
        };
        _db.ArtifactWorkRecords.Add(record);

        // S1 - the record and the promotions it causes are saved together, under concurrency checks
        // on the task/test it is recorded on and on every requirement the promotion decision read
        // (promoted or not): if any of those rows changed since it was read - including another work
        // record's touch, e.g. two developers completing a requirement's last two tasks at once,
        // where neither sees the other's completion - nothing is written (409; the caller reloads
        // and retries, and the retry sees both completions). Not retried automatically.
        _db.MarkArtifactForConcurrencyCheck(artifact);
        await PromoteLinkedFunctionalRequirementsAsync(artifact, record, cancellationToken);
        await ArtifactConcurrency.SaveAsync(_db, cancellationToken);
        return ToResponse(record);
    }

    public async Task<IReadOnlyList<ArtifactWorkRecordResponse>> GetAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == artifactId, cancellationToken)
            ?? throw new KeyNotFoundException($"Artifact {artifactId} not found.");
        await _projectAccess.EnsureMemberAsync(artifact.ProjectId, cancellationToken);
        if (!_currentUser.CanSee(artifact))
        {
            throw new KeyNotFoundException($"Artifact {artifactId} not found.");
        }

        var records = await _db.ArtifactWorkRecords
            .Where(w => w.ArtifactId == artifactId)
            .OrderByDescending(w => w.RecordedAt)
            .ToListAsync(cancellationToken);
        return records.Select(ToResponse).ToList();
    }

    // Applies the promotions to the tracked requirements; the caller saves them with the new record.
    private async Task PromoteLinkedFunctionalRequirementsAsync(Artifact trigger, ArtifactWorkRecord recorded, CancellationToken cancellationToken)
    {
        var frIds = trigger.ArtifactType == ArtifactType.ImplementationTask
            ? await _db.ArtifactRelationships
                .Where(r => r.SourceArtifactId == trigger.Id && r.RelationshipType == RelationshipType.Implements)
                .Select(r => r.TargetArtifactId).ToListAsync(cancellationToken)
            : await _db.ArtifactRelationships
                .Where(r => r.TargetArtifactId == trigger.Id && r.RelationshipType == RelationshipType.TestedBy)
                .Select(r => r.SourceArtifactId).ToListAsync(cancellationToken);

        var frs = await _db.Artifacts
            .Where(a => frIds.Contains(a.Id) && a.ArtifactType == ArtifactType.FunctionalRequirement)
            .ToListAsync(cancellationToken);
        if (frs.Count == 0)
        {
            return;
        }

        // The requirements are read before the work records below: a competing work save that commits
        // after this read has touched the same requirement, so this save's check on it fails (409).
        foreach (var fr in frs)
        {
            _db.MarkArtifactForConcurrencyCheck(fr);
        }

        var links = await _db.ArtifactRelationships
            .Where(r => (r.RelationshipType == RelationshipType.Implements && frIds.Contains(r.TargetArtifactId))
                     || (r.RelationshipType == RelationshipType.TestedBy && frIds.Contains(r.SourceArtifactId)))
            .ToListAsync(cancellationToken);
        var linkedIds = links.Select(r => r.RelationshipType == RelationshipType.Implements ? r.SourceArtifactId : r.TargetArtifactId).ToList();
        // Rejected tasks/tests are a human "no" and don't count either way, as in lifecycle evidence.
        var live = await _db.Artifacts
            .Where(a => linkedIds.Contains(a.Id) && a.Status != ArtifactStatus.Rejected)
            .Select(a => a.Id).ToListAsync(cancellationToken);
        // The new record is not saved yet, so it is added to the stored ones here (exactly once).
        var records = await _db.ArtifactWorkRecords.Where(w => live.Contains(w.ArtifactId) && w.Id != recorded.Id).ToListAsync(cancellationToken);
        if (live.Contains(recorded.ArtifactId))
        {
            records.Add(recorded);
        }

        var outcomes = LifecycleEvidenceService.LatestOutcomes(records);

        bool All(IEnumerable<Guid> ids, WorkOutcome outcome)
        {
            var list = ids.Where(live.Contains).ToList();
            return list.Count > 0 && list.All(id => outcomes.TryGetValue(id, out var o) && o == outcome);
        }

        var now = DateTime.UtcNow;
        foreach (var fr in frs)
        {
            var tasks = links.Where(r => r.RelationshipType == RelationshipType.Implements && r.TargetArtifactId == fr.Id).Select(r => r.SourceArtifactId);
            var tests = links.Where(r => r.RelationshipType == RelationshipType.TestedBy && r.SourceArtifactId == fr.Id).Select(r => r.TargetArtifactId);

            if (ArtifactStatusTransitions.CanSystemPromote(fr.Status, ArtifactStatus.Implemented) && All(tasks, WorkOutcome.TaskCompleted))
            {
                fr.Status = ArtifactStatus.Implemented;
                fr.UpdatedAt = now;
            }

            if (ArtifactStatusTransitions.CanSystemPromote(fr.Status, ArtifactStatus.Verified) && All(tests, WorkOutcome.TestPassed))
            {
                fr.Status = ArtifactStatus.Verified;
                fr.UpdatedAt = now;
            }
        }
    }

    private static ArtifactWorkRecordResponse ToResponse(ArtifactWorkRecord w) =>
        new(w.Id, w.ArtifactId, w.VersionNumber, w.Outcome, w.Note, w.RecordedByUserId, w.RecordedByRole, w.RecordedAt);
}
