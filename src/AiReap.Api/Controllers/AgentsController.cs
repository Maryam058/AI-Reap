using AiReap.Application.Agents;
using AiReap.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

// §36 (REAP-090/091) — the agent chain. Starting a run is a writer action; deciding a stage is
// gated per agent inside AgentOrchestrator (each agent names its own approver roles), so the
// controller only requires an authenticated caller for those endpoints.
[ApiController]
[Authorize]
public class AgentsController : ControllerBase
{
    private const string WriterRoles = $"{Roles.Administrator},{Roles.BusinessAnalyst}";

    private readonly IAgentOrchestrator _orchestrator;

    public AgentsController(IAgentOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    [HttpGet("api/agents")]
    public ActionResult<IReadOnlyList<AgentDefinitionResponse>> GetDefinitions() => Ok(_orchestrator.GetDefinitions());

    [HttpGet("api/projects/{projectId:guid}/agent-runs")]
    public async Task<ActionResult> GetForProject(Guid projectId, CancellationToken cancellationToken) =>
        Ok(await _orchestrator.GetForProjectAsync(projectId, cancellationToken));

    [HttpGet("api/agent-runs/{id:guid}")]
    public async Task<ActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var run = await _orchestrator.GetAsync(id, cancellationToken);
        return run is null ? NotFound() : Ok(run);
    }

    [HttpPost("api/agent-runs")]
    [Authorize(Roles = WriterRoles)]
    public Task<ActionResult> Start([FromBody] StartAgentRunRequest request, CancellationToken cancellationToken) =>
        Handle(() => _orchestrator.StartAsync(request.RequirementSourceId, cancellationToken));

    [HttpPost("api/agent-runs/{id:guid}/decision")]
    public Task<ActionResult> Decide(Guid id, [FromBody] AgentDecisionRequest request, CancellationToken cancellationToken) =>
        Handle(() => _orchestrator.DecideAsync(id, request.Approve, request.Comment, cancellationToken));

    [HttpPost("api/agent-runs/{id:guid}/retry")]
    public Task<ActionResult> Retry(Guid id, CancellationToken cancellationToken) =>
        Handle(() => _orchestrator.RetryAsync(id, cancellationToken));

    private async Task<ActionResult> Handle(Func<Task<AgentRunResponse>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
    }
}
