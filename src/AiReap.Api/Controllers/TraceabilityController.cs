using AiReap.Application.Traceability;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

[ApiController]
[Authorize]
public class TraceabilityController : ControllerBase
{
    private readonly ITraceabilityService _traceability;
    private readonly IImpactAnalysisService _impactAnalysis;

    public TraceabilityController(ITraceabilityService traceability, IImpactAnalysisService impactAnalysis)
    {
        _traceability = traceability;
        _impactAnalysis = impactAnalysis;
    }

    // §21
    [HttpGet("api/projects/{projectId:guid}/traceability-matrix")]
    public async Task<ActionResult<IReadOnlyList<TraceabilityRow>>> GetMatrix(Guid projectId, CancellationToken cancellationToken)
    {
        return Ok(await _traceability.GetMatrixAsync(projectId, cancellationToken));
    }

    // §22
    [HttpGet("api/artifacts/{id:guid}/impact")]
    public async Task<ActionResult<ImpactAnalysisResult>> GetImpact(Guid id, CancellationToken cancellationToken)
    {
        var result = await _impactAnalysis.AnalyzeAsync(id, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}
