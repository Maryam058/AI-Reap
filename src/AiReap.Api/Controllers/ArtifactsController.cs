using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts;
using AiReap.Domain.Common;
using AiReap.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

[ApiController]
[Authorize]
public class ArtifactsController : ControllerBase
{
    private const string WriterRoles = $"{Roles.Administrator},{Roles.BusinessAnalyst}";
    private const string ReviewerRoles = $"{Roles.Administrator},{Roles.BusinessAnalyst},{Roles.Reviewer}";

    private readonly IArtifactService _artifacts;
    private readonly IUserStoryService _userStoryService;
    private readonly ITestGenerationService _testGenerationService;

    public ArtifactsController(IArtifactService artifacts, IUserStoryService userStoryService, ITestGenerationService testGenerationService)
    {
        _artifacts = artifacts;
        _userStoryService = userStoryService;
        _testGenerationService = testGenerationService;
    }

    [HttpGet("api/projects/{projectId:guid}/artifacts")]
    public async Task<ActionResult<IReadOnlyList<ArtifactResponse>>> GetForProject(
        Guid projectId, [FromQuery] ArtifactType? type, [FromQuery] Guid? requirementSourceId, CancellationToken cancellationToken)
    {
        return Ok(await _artifacts.GetForProjectAsync(projectId, type, requirementSourceId, cancellationToken));
    }

    [HttpGet("api/artifacts/{id:guid}")]
    public async Task<ActionResult<ArtifactResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var artifact = await _artifacts.GetByIdAsync(id, cancellationToken);
        return artifact is null ? NotFound() : Ok(artifact);
    }

    // §23 — human edit; creates a new version rather than overwriting history.
    [HttpPatch("api/artifacts/{id:guid}")]
    [Authorize(Roles = WriterRoles)]
    public async Task<ActionResult<ArtifactResponse>> Update(Guid id, UpdateArtifactRequest request, CancellationToken cancellationToken)
    {
        var updated = await _artifacts.UpdateAsync(id, request, cancellationToken);
        return updated is null ? NotFound() : Ok(updated);
    }

    // §24 — Approved/Rejected are review decisions, gated to reviewer-capable roles.
    [HttpPatch("api/artifacts/{id:guid}/status")]
    [Authorize(Roles = ReviewerRoles)]
    public async Task<ActionResult<ArtifactResponse>> UpdateStatus(Guid id, UpdateArtifactStatusRequest request, CancellationToken cancellationToken)
    {
        var updated = await _artifacts.UpdateStatusAsync(id, request, cancellationToken);
        return updated is null ? NotFound() : Ok(updated);
    }

    // §8 — answering a clarification question.
    [HttpPost("api/artifacts/{id:guid}/clarification-answer")]
    [Authorize(Roles = WriterRoles)]
    public async Task<ActionResult<ArtifactResponse>> AnswerClarification(Guid id, ClarificationAnswerRequest request, CancellationToken cancellationToken)
    {
        var updated = await _artifacts.AnswerClarificationAsync(id, request, cancellationToken);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpGet("api/artifacts/{id:guid}/versions")]
    public async Task<ActionResult<IReadOnlyList<ArtifactVersionResponse>>> GetVersions(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await _artifacts.GetVersionsAsync(id, cancellationToken));
    }

    [HttpGet("api/artifacts/{id:guid}/relationships")]
    public async Task<ActionResult<IReadOnlyList<ArtifactRelationshipResponse>>> GetRelationships(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await _artifacts.GetRelationshipsAsync(id, cancellationToken));
    }

    // §13 — only valid when id is a UserStory artifact.
    [HttpPost("api/artifacts/{id:guid}/generate-acceptance-criteria")]
    [Authorize(Roles = WriterRoles)]
    public async Task<ActionResult> GenerateAcceptanceCriteria(Guid id, CancellationToken cancellationToken)
    {
        var created = await _userStoryService.GenerateAcceptanceCriteriaAsync(id, cancellationToken);
        return created is null ? NotFound("Artifact not found or is not a UserStory.") : Ok(created);
    }

    // §20 — only valid when id is a FunctionalRequirement artifact.
    [HttpPost("api/artifacts/{id:guid}/generate-test-cases")]
    [Authorize(Roles = WriterRoles)]
    public async Task<ActionResult> GenerateTestCases(Guid id, CancellationToken cancellationToken)
    {
        var created = await _testGenerationService.GenerateAsync(id, cancellationToken);
        return created is null ? NotFound("Artifact not found or is not a FunctionalRequirement.") : Ok(created);
    }
}
