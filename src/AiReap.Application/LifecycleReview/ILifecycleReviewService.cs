namespace AiReap.Application.LifecycleReview;

public interface ILifecycleReviewService
{
    // Throws KeyNotFoundException (unknown project) and ProjectAccessDeniedException (not a member).
    Task<LifecycleReviewResponse> GetAsync(Guid projectId, CancellationToken cancellationToken = default);
}
