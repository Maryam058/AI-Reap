namespace AiReap.Application.Lifecycle;

// §5 — the canonical project lifecycle. Throws KeyNotFoundException (unknown project/transition),
// ProjectAccessDeniedException (not a member), UnauthorizedAccessException (role or separation of
// duties), RequestValidationException (missing mandatory reason), ConflictException (wrong state,
// skipped stage, pending request, lost race) and LifecycleEvidenceException (a ConflictException
// carrying the failing exit checks).
public interface IProjectLifecycleService
{
    Task<ProjectLifecycleState> GetStateAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StageTransitionResponse>> GetHistoryAsync(Guid projectId, CancellationToken cancellationToken = default);

    // Direct stages move immediately; gated stages (Design, Testing) create a pending request.
    Task<StageTransitionResponse> AdvanceAsync(Guid projectId, AdvanceStageRequest request, CancellationToken cancellationToken = default);

    Task<StageTransitionResponse> DecideAsync(Guid transitionId, DecideStageTransitionRequest request, CancellationToken cancellationToken = default);

    Task<StageTransitionResponse> WithdrawAsync(Guid transitionId, string? note, CancellationToken cancellationToken = default);

    // Comment-only review of a pending request (any project member).
    Task<StageReviewResponse> CommentAsync(Guid transitionId, string? comment, CancellationToken cancellationToken = default);

    Task<StageTransitionResponse> RollbackAsync(Guid projectId, RollbackStageRequest request, CancellationToken cancellationToken = default);
}
