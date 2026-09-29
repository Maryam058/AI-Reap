using AiReap.Api.Authorization;
using AiReap.Application.Lifecycle;
using AiReap.Domain.Common;
using AiReap.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

// §5 — the project lifecycle over HTTP. Thin by design: every rule (stage ownership, gates,
// separation of duties, evidence, override, rollback, concurrency, notifications) lives in
// ProjectLifecycleService. Stage ownership depends on the project's current stage, so it can't be a
// static [Authorize(Roles)] here; the service enforces it and ApplicationExceptionHandler maps its
// failures: wrong role -> 403, missing reason -> 400, wrong state / evidence / lost race -> 409.
// Where a role gate *is* static (deciding a gate, rolling back) it is also declared below, both as
// defence in depth and so GET /api/roles/matrix reports it.
[ApiController]
[Authorize]
public class LifecycleController : ControllerBase
{
    private const string GateDeciders = $"{Roles.Reviewer},{Roles.Administrator}";

    private readonly IProjectLifecycleService _lifecycle;

    public LifecycleController(IProjectLifecycleService lifecycle)
    {
        _lifecycle = lifecycle;
    }

    // Who owns each stage and which moves are gated - the policy table, for the UI and roles page.
    [HttpGet("api/lifecycle/policy")]
    public ActionResult<IReadOnlyList<LifecycleStagePolicyResponse>> GetPolicy() =>
        Ok(ProjectLifecyclePolicy.Stages.Select(s => new LifecycleStagePolicyResponse(
            s.Status, s.Label, s.OwnerRole,
            ProjectLifecyclePolicy.Next(s.Status),
            ProjectLifecyclePolicy.IsGated(s.Status),
            ProjectLifecyclePolicy.IsGated(s.Status) ? ProjectLifecyclePolicy.GateApproverRole : null)).ToList());

    [HttpGet("api/projects/{projectId:guid}/lifecycle")]
    [Capability("Lifecycle", "View the project's stage, exit checks, pending request and history", 100)]
    public Task<ActionResult> GetState(Guid projectId, CancellationToken cancellationToken) =>
        Handle(async () => Ok(await _lifecycle.GetStateAsync(projectId, cancellationToken)));

    [HttpGet("api/projects/{projectId:guid}/lifecycle/history")]
    public Task<ActionResult> GetHistory(Guid projectId, CancellationToken cancellationToken) =>
        Handle(async () => Ok(await _lifecycle.GetHistoryAsync(projectId, cancellationToken)));

    // Direct stages move immediately (201, state Completed); Design and Testing create a pending
    // request for a Reviewer (201, state Pending) and leave Project.Status unchanged.
    [HttpPost("api/projects/{projectId:guid}/lifecycle/transitions")]
    [Capability("Lifecycle", "Advance or submit the current stage (the stage's owner role; an Administrator only as an override, with a reason)", 110)]
    public Task<ActionResult> Advance(Guid projectId, AdvanceStageRequest request, CancellationToken cancellationToken) =>
        Handle(async () =>
        {
            var transition = await _lifecycle.AdvanceAsync(projectId, request, cancellationToken);
            return Created($"/api/projects/{projectId}/lifecycle/history", transition);
        });

    [HttpPost("api/stage-transitions/{id:guid}/decision")]
    [Authorize(Roles = GateDeciders)]
    [Capability("Lifecycle", "Approve or reject a Design or Testing stage request (not your own)", 120)]
    public Task<ActionResult> Decide(Guid id, DecideStageTransitionRequest request, CancellationToken cancellationToken) =>
        Handle(async () => Ok(await _lifecycle.DecideAsync(id, request, cancellationToken)));

    [HttpPost("api/stage-transitions/{id:guid}/withdraw")]
    public Task<ActionResult> Withdraw(Guid id, WithdrawStageTransitionRequest? request, CancellationToken cancellationToken) =>
        Handle(async () => Ok(await _lifecycle.WithdrawAsync(id, request?.Note, cancellationToken)));

    [HttpPost("api/stage-transitions/{id:guid}/comments")]
    [Capability("Lifecycle", "Comment on a pending stage request", 130)]
    public Task<ActionResult> Comment(Guid id, StageCommentRequest request, CancellationToken cancellationToken) =>
        Handle(async () => StatusCode(StatusCodes.Status201Created, await _lifecycle.CommentAsync(id, request.Comment, cancellationToken)));

    [HttpPost("api/projects/{projectId:guid}/lifecycle/rollback")]
    [Authorize(Roles = Roles.Administrator)]
    [Capability("Lifecycle", "Roll a project back to an earlier stage, or override failing exit checks (with a reason)", 140)]
    public Task<ActionResult> Rollback(Guid projectId, RollbackStageRequest request, CancellationToken cancellationToken) =>
        Handle(async () => Ok(await _lifecycle.RollbackAsync(projectId, request, cancellationToken)));

    // Unknown project / transition -> 404. Everything else is mapped by ApplicationExceptionHandler.
    private async Task<ActionResult> Handle(Func<Task<ActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}

public record LifecycleStagePolicyResponse(
    ProjectStatus Status, string Label, string OwnerRole, ProjectStatus? Next, bool LeavingIsGated, string? GateApproverRole);
