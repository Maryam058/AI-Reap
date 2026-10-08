using AiReap.Api.Authorization;
using AiReap.Application.LifecycleReview;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

// Read-only stage-by-stage review of a project's lifecycle (what each stage produced, by whom, and
// how it was traced and approved). Membership is enforced by ProjectMembershipFilter on {projectId};
// what the caller sees inside follows ArtifactVisibility, as on every other artifact route.
[ApiController]
[Authorize]
public class LifecycleReviewController : ControllerBase
{
    private readonly ILifecycleReviewService _review;

    public LifecycleReviewController(ILifecycleReviewService review)
    {
        _review = review;
    }

    [HttpGet("api/projects/{projectId:guid}/lifecycle-review")]
    [Capability("Lifecycle", "Review the artifacts and decisions of each lifecycle stage", 105)]
    public async Task<ActionResult<LifecycleReviewResponse>> Get(Guid projectId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _review.GetAsync(projectId, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
