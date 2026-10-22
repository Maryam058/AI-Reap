using AiReap.Api.Authorization;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Files;
using AiReap.Application.RequirementSources;
using AiReap.Domain.Common;
using AiReap.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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

    // §6 — TXT/PDF/DOCX upload (ADR-001 §4). The content must be what the name says (signature and a
    // document that opens; InvalidDocumentException), within OriginalDocuments:MaxUploadBytes. A PDF or
    // DOCX is also kept as the original, privately (docs/SOURCE_DOCUMENT_RETENTION.md); a TXT's text is
    // the file. The file name is kept as display text only.
    [HttpPost("api/projects/{projectId:guid}/requirement-sources/upload")]
    [Authorize(Roles = WriterRoles)]
    [UploadSizeLimit]
    public async Task<ActionResult<RequirementSourceResponse>> Upload(
        Guid projectId, IFormFile file, CancellationToken cancellationToken, [FromServices] IOptions<OriginalDocumentOptions>? originalOptions = null)
    {
        if (file.Length == 0)
        {
            return BadRequest("File is empty.");
        }

        var maxBytes = (originalOptions?.Value ?? new OriginalDocumentOptions()).MaxUploadBytes;
        if (file.Length > maxBytes)
        {
            return Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "File too large.",
                detail: $"Uploads are limited to {maxBytes / (1024.0 * 1024.0):0.#} MB.");
        }

        if (!_textExtractor.CanExtract(file.FileName))
        {
            return BadRequest("Only .txt, .pdf, and .docx uploads are supported.");
        }

        var content = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
        {
            await stream.ReadExactlyAsync(content, cancellationToken);
        }

        ExtractedDocument extracted;
        try
        {
            extracted = await _textExtractor.ExtractDocumentAsync(content, file.FileName, cancellationToken);
        }
        catch (InvalidDocumentException ex)
        {
            return BadRequest(ex.Message);
        }

        if (string.IsNullOrWhiteSpace(extracted.Text))
        {
            return BadRequest("No extractable text was found in this file (a scanned/image-only PDF is not supported).");
        }

        var original = extracted.Format is null ? null : new OriginalDocumentUpload(content, extracted.Format, extracted.PageLineStarts);
        var created = await _sources.CreateFromUploadAsync(projectId, file.FileName, extracted.Text, original, cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    // The original PDF/DOCX of a source uploaded before originals were kept, offered again so the source
    // viewer can show it. Checked as an upload is, and accepted only when its text is the source's text
    // (AttachOriginalAsync); the source's text and everything generated from it are left as they are.
    [HttpPost("api/requirement-sources/{id:guid}/original")]
    [Authorize(Roles = WriterRoles)]
    [UploadSizeLimit]
    public async Task<ActionResult<RequirementSourceResponse>> AttachOriginal(
        Guid id, IFormFile file, CancellationToken cancellationToken, [FromServices] IOptions<OriginalDocumentOptions>? originalOptions = null)
    {
        var maxBytes = (originalOptions?.Value ?? new OriginalDocumentOptions()).MaxUploadBytes;
        if (file.Length == 0)
        {
            return BadRequest("File is empty.");
        }

        if (file.Length > maxBytes)
        {
            return Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "File too large.",
                detail: $"Uploads are limited to {maxBytes / (1024.0 * 1024.0):0.#} MB.");
        }

        if (!_textExtractor.CanExtract(file.FileName))
        {
            return BadRequest("Only .pdf and .docx files can be attached as the original.");
        }

        var content = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
        {
            await stream.ReadExactlyAsync(content, cancellationToken);
        }

        ExtractedDocument extracted;
        try
        {
            extracted = await _textExtractor.ExtractDocumentAsync(content, file.FileName, cancellationToken);
        }
        catch (InvalidDocumentException ex)
        {
            return BadRequest(ex.Message);
        }

        var result = await _sources.AttachOriginalAsync(id, content, extracted, cancellationToken);
        return result.Status switch
        {
            AttachOriginalStatus.Attached => Ok(result.Source),
            AttachOriginalStatus.SourceNotFound => NotFound(),
            AttachOriginalStatus.NotApplicable => Problem(statusCode: StatusCodes.Status409Conflict, title: "No original can be attached.",
                detail: "This source already has its original, was not uploaded as a file of this type, or originals are not kept."),
            _ => Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "This is not the uploaded document.",
                detail: "The file's text is not the text of this source. Choose the same version of the document that was uploaded.")
        };
    }

    // The original PDF/DOCX of an uploaded source, for the in-app source viewer: membership and the
    // raw-source rule (S2) as for the source's text, but a source in another project is a plain 404,
    // the same as one that does not exist. Sent inline, never cached, and never sniffed as another type;
    // the sandbox policy keeps it inert if it is opened directly.
    [HttpGet("api/requirement-sources/{id:guid}/original")]
    public async Task<IActionResult> GetOriginal(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sources.GetOriginalAsync(id, cancellationToken);
        switch (result.Status)
        {
            case OriginalDocumentStatus.SourceNotFound:
                return NotFound();
            case OriginalDocumentStatus.NotRetained:
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "Original document not kept.",
                    detail: "Only the text of this source is kept: it was pasted, uploaded as TXT, or uploaded before original files were kept.",
                    type: "urn:aireap:original-not-retained");
            case OriginalDocumentStatus.Unavailable:
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "Original document unavailable.",
                    detail: "The original file of this source could not be read. Its text is still available.",
                    type: "urn:aireap:original-unavailable");
        }

        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-site";
        var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(ContentDispositionFileName(result.FileName));
        Response.Headers.ContentDisposition = disposition.ToString();
        return File(result.Content!, result.ContentType!);
    }

    // The display name as a header-safe file name: no path, no control characters.
    private static string ContentDispositionFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        name = new string(name.Where(c => !char.IsControl(c) && c is not ('"' or '\\')).ToArray()).Trim();
        return name.Length == 0 ? "requirement-source" : name;
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
