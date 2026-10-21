using AiReap.Application.Ai.Pipeline;
using AiReap.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

// §16-§19 — Phase 2 design and planning generators, all scoped to a requirement source.
[ApiController]
[Authorize]
public class DesignController : ControllerBase
{
    private const string WriterRoles = $"{Roles.Administrator},{Roles.BusinessAnalyst}";

    private readonly ISolutionDesignService _solutionDesignService;
    private readonly IDatabaseDesignService _databaseDesignService;
    private readonly IApiDesignService _apiDesignService;
    private readonly IImplementationPlanningService _implementationPlanningService;

    public DesignController(
        ISolutionDesignService solutionDesignService,
        IDatabaseDesignService databaseDesignService,
        IApiDesignService apiDesignService,
        IImplementationPlanningService implementationPlanningService)
    {
        _solutionDesignService = solutionDesignService;
        _databaseDesignService = databaseDesignService;
        _apiDesignService = apiDesignService;
        _implementationPlanningService = implementationPlanningService;
    }

    // §16
    [HttpPost("api/requirement-sources/{id:guid}/generate-design")]
    [Authorize(Roles = WriterRoles)]
    public async Task<ActionResult> GenerateDesign(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var (design, created) = await _solutionDesignService.GenerateWithOutcomeAsync(id, cancellationToken);
            GenerationCountHeaders.Write(Response, created ? 1 : 0, created ? 0 : 1);
            return Ok(design);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // §17
    [HttpPost("api/requirement-sources/{id:guid}/generate-data-entities")]
    [Authorize(Roles = WriterRoles)]
    public async Task<ActionResult> GenerateDataEntities(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _databaseDesignService.GenerateAsync(id, cancellationToken);
            GenerationCountHeaders.Write(Response, result);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // §18
    [HttpPost("api/requirement-sources/{id:guid}/generate-api-specs")]
    [Authorize(Roles = WriterRoles)]
    public async Task<ActionResult> GenerateApiSpecs(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _apiDesignService.GenerateAsync(id, cancellationToken);
            GenerationCountHeaders.Write(Response, result);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // §19
    [HttpPost("api/requirement-sources/{id:guid}/generate-tasks")]
    [Authorize(Roles = WriterRoles)]
    public async Task<ActionResult> GenerateTasks(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _implementationPlanningService.GenerateAsync(id, cancellationToken);
            GenerationCountHeaders.Write(Response, result);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
