using AiReap.Application.Ai.Pipeline;
using AiReap.Domain.Common;
using AiReap.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

// Batched (resumable) analysis / requirement generation of one requirement source - see
// AiChunkRunService. Only when Ai:Groq:MaxBatchedRunParts > 0; otherwise every action answers 400
// batch_mode_disabled without touching the AiChunkRuns tables. Running a step is for the roles that
// run Analyze / Generate; reading a run is for any project member (checked in the service).
[ApiController]
[Authorize]
public class AiRunsController : ControllerBase
{
    private const string WriterRoles = $"{Roles.Administrator},{Roles.BusinessAnalyst}";

    private readonly IAiChunkRunService _runs;

    public AiRunsController(IAiChunkRunService runs)
    {
        _runs = runs;
    }

    public sealed record StartAiRunRequest(string? Operation);

    [HttpPost("api/requirement-sources/{id:guid}/ai-runs")]
    [Authorize(Roles = WriterRoles)]
    public async Task<ActionResult<AiChunkRunResponse>> Start(Guid id, StartAiRunRequest request, CancellationToken cancellationToken)
    {
        if (ParseOperation(request.Operation) is not { } operation)
        {
            return UnknownOperation();
        }

        return await Handle(async () =>
        {
            var run = await _runs.StartAsync(id, operation, cancellationToken);
            return CreatedAtAction(nameof(Get), new { runId = run.Id }, run);
        });
    }

    // The active (Running or Staged) run of an operation for a source; 204 when there is none.
    [HttpGet("api/requirement-sources/{id:guid}/ai-runs")]
    public async Task<ActionResult<AiChunkRunResponse>> GetActive(Guid id, [FromQuery] string? operation, CancellationToken cancellationToken)
    {
        if (ParseOperation(operation) is not { } op)
        {
            return UnknownOperation();
        }

        return await Handle(async () =>
            await _runs.GetActiveAsync(id, op, cancellationToken) is { } run ? Ok(run) : NoContent());
    }

    [HttpGet("api/ai-runs/{runId:guid}")]
    public Task<ActionResult<AiChunkRunResponse>> Get(Guid runId, CancellationToken cancellationToken) =>
        Handle(async () => Ok(await _runs.GetAsync(runId, cancellationToken)));

    [HttpPost("api/ai-runs/{runId:guid}/continue")]
    [Authorize(Roles = WriterRoles)]
    public Task<ActionResult<AiChunkRunResponse>> Continue(Guid runId, CancellationToken cancellationToken) =>
        Handle(async () => Ok(await _runs.ContinueAsync(runId, cancellationToken)));

    [HttpPost("api/ai-runs/{runId:guid}/commit")]
    [Authorize(Roles = WriterRoles)]
    public Task<ActionResult<AiChunkRunResponse>> Commit(Guid runId, CancellationToken cancellationToken) =>
        Handle(async () => Ok(await _runs.CommitAsync(runId, cancellationToken)));

    [HttpPost("api/ai-runs/{runId:guid}/abandon")]
    [Authorize(Roles = WriterRoles)]
    public Task<ActionResult<AiChunkRunResponse>> Abandon(Guid runId, CancellationToken cancellationToken) =>
        Handle(async () => Ok(await _runs.AbandonAsync(runId, cancellationToken)));

    private static AiChunkRunOperation? ParseOperation(string? operation) =>
        Enum.TryParse<AiChunkRunOperation>(operation, ignoreCase: true, out var op) && Enum.IsDefined(op) ? op : null;

    private BadRequestObjectResult UnknownOperation() => BadRequest(new ProblemDetails
    {
        Title = "Unknown operation.",
        Detail = "operation must be 'analyze' or 'generate'.",
        Status = StatusCodes.Status400BadRequest
    });

    // Not-found maps here; batch and conflict errors go through ApplicationExceptionHandler.
    private async Task<ActionResult<AiChunkRunResponse>> Handle(Func<Task<ActionResult>> action)
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
