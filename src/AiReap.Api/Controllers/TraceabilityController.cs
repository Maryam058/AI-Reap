using AiReap.Application.Traceability;
using AiReap.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

[ApiController]
[Authorize]
public class TraceabilityController : ControllerBase
{
    private const string WriterRoles = $"{Roles.Administrator},{Roles.BusinessAnalyst}";
    private const string ReviewRoles = $"{Roles.Administrator},{Roles.BusinessAnalyst},{Roles.Reviewer},{Roles.Developer},{Roles.QA}";

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

    // §21 — objectives (seeded from the project's Objectives field) and the requirements serving each.
    [HttpGet("api/projects/{projectId:guid}/business-objectives")]
    public async Task<ActionResult<IReadOnlyList<BusinessObjectiveResponse>>> GetBusinessObjectives(Guid projectId, CancellationToken cancellationToken)
    {
        return Ok(await _traceability.GetBusinessObjectivesAsync(projectId, cancellationToken));
    }

    [HttpPut("api/artifacts/{id:guid}/business-objectives")]
    [Authorize(Roles = WriterRoles)]
    public async Task<ActionResult<IReadOnlyList<TraceRef>>> SetBusinessObjectives(Guid id, SetBusinessObjectivesRequest request, CancellationToken cancellationToken)
    {
        var linked = await _traceability.SetBusinessObjectivesAsync(id, request.BusinessObjectiveIds ?? [], cancellationToken);
        return linked is null ? NotFound("Artifact not found or is not a requirement.") : Ok(linked);
    }

    // §22
    [HttpGet("api/artifacts/{id:guid}/impact")]
    public async Task<ActionResult<ImpactAnalysisResult>> GetImpact(Guid id, CancellationToken cancellationToken)
    {
        var result = await _impactAnalysis.AnalyzeAsync(id, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    // §22 — persistent "an upstream artifact changed" flags, raised automatically when approved content changes.
    [HttpGet("api/projects/{projectId:guid}/impact-notices")]
    public async Task<ActionResult<IReadOnlyList<ImpactNoticeResponse>>> GetProjectImpactNotices(
        Guid projectId, [FromQuery] bool includeAcknowledged, CancellationToken cancellationToken)
    {
        return Ok(await _impactAnalysis.GetNoticesForProjectAsync(projectId, includeAcknowledged, cancellationToken));
    }

    [HttpGet("api/artifacts/{id:guid}/impact-notices")]
    public async Task<ActionResult<IReadOnlyList<ImpactNoticeResponse>>> GetArtifactImpactNotices(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await _impactAnalysis.GetNoticesForArtifactAsync(id, cancellationToken));
    }

    // Any project role that owns downstream work (BA, Reviewer, Developer, QA) can confirm they've
    // reviewed their artifact against the upstream change.
    [HttpPost("api/impact-notices/{id:guid}/acknowledge")]
    [Authorize(Roles = ReviewRoles)]
    public async Task<ActionResult<ImpactNoticeResponse>> Acknowledge(Guid id, AcknowledgeImpactNoticeRequest request, CancellationToken cancellationToken)
    {
        var notice = await _impactAnalysis.AcknowledgeAsync(id, request.Note, cancellationToken);
        return notice is null ? NotFound() : Ok(notice);
    }
}
