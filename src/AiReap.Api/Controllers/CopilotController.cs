using AiReap.Api.Authorization;
using AiReap.Application.Knowledge;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

[ApiController]
[Authorize]
public class CopilotController : ControllerBase
{
    private readonly ICopilotService _copilot;

    public CopilotController(ICopilotService copilot)
    {
        _copilot = copilot;
    }

    // §25 — read-only, so open to any authenticated project role (no role restriction).
    [HttpPost("api/projects/{projectId:guid}/copilot/ask")]
    [Capability("View", "Ask the AI assistant and view the AI audit trail", 20)]
    public async Task<ActionResult<CopilotAnswerResponse>> Ask(Guid projectId, AskCopilotRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return BadRequest("Question is required.");
        }

        return Ok(await _copilot.AskAsync(projectId, request.Question, cancellationToken));
    }

    // The question-length limit Ask enforces (Ai:Copilot:MaxQuestionChars), so the question input can
    // apply the same limit. Not a separate capability - part of asking the assistant.
    [HttpGet("api/projects/{projectId:guid}/copilot/limits")]
    public ActionResult<CopilotLimitsResponse> Limits(Guid projectId) => Ok(_copilot.GetLimits());
}
