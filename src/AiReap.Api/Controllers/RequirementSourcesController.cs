using AiReap.Api.Authorization;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Files;
using AiReap.Application.RequirementSources;
using AiReap.Domain.Common;
using AiReap.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

[ApiController]
[Authorize]
public class RequirementSourcesController : ControllerBase
{
    private const string WriterRoles = $"{Roles.Administrator},{Roles.BusinessAnalyst}";
    private const string ReviewerRoles = $"{Roles.Administrator},{Roles.BusinessAnalyst},{Roles.Reviewer}";

    private readonly IRequirementSourceService _sources;
    private readonly IRequirementAnalysisService _analysisService;
    private readonly IClarificationService _clarificationService;
    private readonly IRequirementGenerationService _requirementGenerationService;
    private readonly IUserStoryService _userStoryService;
    private readonly IBusinessRuleService _businessRuleService;
    private readonly IRequirementQualityService _qualityService;
    private readonly IDocumentTextExtractor _textExtractor;
    private readonly IAiPlanService _aiPlanService;

    public RequirementSourcesController(
        IRequirementSourceService sources,
        IRequirementAnalysisService analysisService,
        IClarificationService clarificationService,
        IRequirementGenerationService requirementGenerationService,
        IUserStoryService userStoryService,
        IBusinessRuleService businessRuleService,
        IRequirementQualityService qualityService,
        IDocumentTextExtractor textExtractor,
        IAiPlanService aiPlanService)
    {
        _sources = sources;
        _analysisService = analysisService;
        _clarificationService = clarificationService;
        _requirementGenerationService = requirementGenerationService;
        _userStoryService = userStoryService;
        _businessRuleService = businessRuleService;
        _qualityService = qualityService;
        _textExtractor = textExtractor;
        _aiPlanService = aiPlanService;
    }

    // §6 — manual entry or pasted meeting notes.
    [HttpPost("api/projects/{projectId:guid}/requirement-sources")]
    [Authorize(Roles = WriterRoles)]
    [Capability("Author", "Add requirement sources and run AI analysis and generation", 40)]
    public async Task<ActionResult<RequirementSourceResponse>> Create(
        Guid projectId, CreateRequirementSourceRequest request, CancellationToken cancellationToken)
    {
        var created = await _sources.CreateAsync(projectId, request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    // §6 — TXT/PDF/DOCX upload (ADR-001 §4).
    [HttpPost("api/projects/{projectId:guid}/requirement-sources/upload")]
    [Authorize(Roles = WriterRoles)]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<ActionResult<RequirementSourceResponse>> Upload(
        Guid projectId, IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            return BadRequest("File is empty.");
        }

        if (!_textExtractor.CanExtract(file.FileName))
        {
            return BadRequest("Only .txt, .pdf, and .docx uploads are supported.");
        }

        string text;
        using (var stream = file.OpenReadStream())
        {
            text = await _textExtractor.ExtractAsync(stream, file.FileName, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return BadRequest("No extractable text was found in this file (a scanned/image-only PDF is not supported).");
        }

        var created = await _sources.CreateAsync(
            projectId,
            new CreateRequirementSourceRequest(RequirementSourceType.FileUpload, text, file.FileName),
            cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpGet("api/projects/{projectId:guid}/requirement-sources")]
    public async Task<ActionResult<IReadOnlyList<RequirementSourceResponse>>> GetForProject(
        Guid projectId, CancellationToken cancellationToken)
    {
        return Ok(await _sources.GetForProjectAsync(projectId, cancellationToken));
    }

    [HttpGet("api/requirement-sources/{id:guid}")]
    public async Task<ActionResult<RequirementSourceResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var source = await _sources.GetByIdAsync(id, cancellationToken);
        return source is null ? NotFound() : Ok(source);
    }

    // P0-4 — which requirement IDs written in the source no saved, non-rejected FR/NFR cites. Read-only;
    // computed from the source text and the current requirements. Refused (403) to a caller who may
    // not read raw sources (S2).
    [HttpGet("api/requirement-sources/{id:guid}/source-id-coverage")]
    public async Task<ActionResult<SourceIdCoverage>> GetSourceIdCoverage(Guid id, CancellationToken cancellationToken)
    {
        var coverage = await _sources.GetSourceIdCoverageAsync(id, cancellationToken);
        return coverage is null ? NotFound() : Ok(coverage);
    }

    // §7/§8 — analyze the raw source and generate clarification questions for anything missing.
    [HttpPost("api/requirement-sources/{id:guid}/analyze")]
    [Authorize(Roles = WriterRoles)]
    public async Task<ActionResult<AnalyzeRequirementResult>> Analyze(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _analysisService.AnalyzeAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // Dry run for Analyze / Generate requirements: how each configured AI provider would take this
    // source (whole, in N parts, or refused) and why. Sends nothing to any AI provider and saves
    // nothing; token figures are estimates and remaining provider quota is unknown.
    [HttpGet("api/requirement-sources/{id:guid}/ai-plan")]
    public async Task<ActionResult<AiPlanResult>> GetAiPlan(Guid id, [FromQuery] string? operation, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<AiPlanOperation>(operation, ignoreCase: true, out var op) || !Enum.IsDefined(op))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Unknown operation.",
                Detail = "operation must be 'analyze' or 'generate'.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        try
        {
            return Ok(await _aiPlanService.PlanAsync(id, op, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("api/requirement-sources/{id:guid}/clarification-questions")]
    public async Task<ActionResult> GetClarificationQuestions(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await _clarificationService.GetForRequirementSourceAsync(id, cancellationToken));
    }

    // §9/§10
    [HttpPost("api/requirement-sources/{id:guid}/generate-requirements")]
    [Authorize(Roles = WriterRoles)]
    public async Task<ActionResult<GenerateRequirementsResult>> GenerateRequirements(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _requirementGenerationService.GenerateAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // §12
    [HttpPost("api/requirement-sources/{id:guid}/generate-user-stories")]
    [Authorize(Roles = WriterRoles)]
    public async Task<ActionResult> GenerateUserStories(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _userStoryService.GenerateStoriesAsync(id, cancellationToken);
            GenerationCountHeaders.Write(Response, result);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // §11
    [HttpPost("api/requirement-sources/{id:guid}/generate-business-rules")]
    [Authorize(Roles = WriterRoles)]
    public async Task<ActionResult> GenerateBusinessRules(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _businessRuleService.GenerateAsync(id, cancellationToken);
            GenerationCountHeaders.Write(Response, result);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // §14 — read-only analysis; does not create or change any artifact.
    [HttpPost("api/requirement-sources/{id:guid}/analyze-quality")]
    [Authorize(Roles = ReviewerRoles)]
    public async Task<ActionResult> AnalyzeQuality(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _qualityService.AnalyzeAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
