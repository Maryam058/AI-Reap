using AiReap.Application.Files;
using AiReap.Application.Knowledge;
using AiReap.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

[ApiController]
[Authorize]
public class DocumentsController : ControllerBase
{
    private const string WriterRoles = $"{Roles.Administrator},{Roles.BusinessAnalyst}";

    private readonly IDocumentService _documents;
    private readonly IDocumentTextExtractor _textExtractor;

    public DocumentsController(IDocumentService documents, IDocumentTextExtractor textExtractor)
    {
        _documents = documents;
        _textExtractor = textExtractor;
    }

    // §26 — TXT/PDF/DOCX upload (ADR-001 §4).
    [HttpPost("api/projects/{projectId:guid}/documents/upload")]
    [Authorize(Roles = WriterRoles)]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<ActionResult<DocumentResponse>> Upload(Guid projectId, IFormFile file, CancellationToken cancellationToken)
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

        var created = await _documents.UploadAsync(projectId, file.FileName, file.ContentType, text, cancellationToken);
        return CreatedAtAction(nameof(GetForProject), new { projectId }, created);
    }

    [HttpGet("api/projects/{projectId:guid}/documents")]
    public async Task<ActionResult<IReadOnlyList<DocumentResponse>>> GetForProject(Guid projectId, CancellationToken cancellationToken)
    {
        return Ok(await _documents.GetForProjectAsync(projectId, cancellationToken));
    }
}
