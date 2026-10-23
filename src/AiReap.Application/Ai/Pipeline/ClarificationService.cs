using System.Text.Json;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Ai.Pipeline;

public class ClarificationService : IClarificationService
{
    private readonly IAiReapDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly ICurrentUser _currentUser;

    public ClarificationService(IAiReapDbContext db, IProjectAccessService projectAccess, ICurrentUser currentUser)
    {
        _db = db;
        _projectAccess = projectAccess;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<ArtifactResponse>> GetForRequirementSourceAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.AsNoTracking()
            .Where(s => s.Id == requirementSourceId)
            .Select(s => new { s.ProjectId, s.RawText })
            .FirstOrDefaultAsync(cancellationToken);
        if (source is not null)
        {
            await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);
        }

        var questions = await _db.Artifacts.VisibleTo(_currentUser)
            .Where(a => a.RequirementSourceId == requirementSourceId && a.ArtifactType == ArtifactType.ClarificationQuestion)
            .OrderBy(a => a.Code)
            .ToListAsync(cancellationToken);

        // Source references point into the raw source, so a restricted caller (S2) gets none; a
        // question saved before they were resolved gets them resolved now (ClarificationSourceReferences).
        if (!_currentUser.CanReadRawSources())
        {
            return questions.Select(q => ArtifactResponseMapper.ToResponse(q) with
            {
                Data = ArtifactJson.ToCamelCaseElement(RequirementEvidenceFields.RedactForRestrictedCaller(q.DataJson))
            }).ToList();
        }

        // Every question is linked to its source as far as the source verifies; one saved without located
        // evidence gets it recovered from the analysis response that raised it, when that is kept.
        var locator = ClarificationEvidenceLocator.ForSource(requirementSourceId, source?.RawText);
        var traces = await TracesAsync(questions.Where(q => ClarificationSourceReferences.NeedsEvidence(q.DataJson)).ToList(), cancellationToken);
        return questions.Select(q => ArtifactResponseMapper.ToResponse(q) with
        {
            Data = ArtifactJson.ToCamelCaseElement(
                ClarificationSourceReferences.WithSourceLinks(q.DataJson, requirementSourceId, locator, traces.GetValueOrDefault(q.Id)))
        }).ToList();
    }

    // For each question: the RequirementAnalysis responses its audit rows kept (AIExecution, one row per
    // produced question) and its wording as first saved, when it has been edited since.
    private async Task<Dictionary<Guid, ClarificationSourceReferences.GenerationTrace>> TracesAsync(
        List<Artifact> questions, CancellationToken cancellationToken)
    {
        if (questions.Count == 0)
        {
            return [];
        }

        var ids = questions.Select(q => q.Id).ToList();
        var outputs = await _db.AIExecutions.AsNoTracking()
            .Where(e => e.OperationType == "RequirementAnalysis" && e.ProducedArtifactId != null && ids.Contains(e.ProducedArtifactId.Value))
            .Select(e => new { ArtifactId = e.ProducedArtifactId!.Value, e.OutputJson })
            .ToListAsync(cancellationToken);
        var edited = questions.Where(q => q.CurrentVersion > 1).Select(q => q.Id).ToList();
        var firstVersions = edited.Count == 0
            ? []
            : await _db.ArtifactVersions.AsNoTracking()
                .Where(v => v.VersionNumber == 1 && edited.Contains(v.ArtifactId))
                .Select(v => new { v.ArtifactId, v.DataSnapshotJson })
                .ToListAsync(cancellationToken);

        return questions.ToDictionary(q => q.Id, q => new ClarificationSourceReferences.GenerationTrace(
            outputs.Where(o => o.ArtifactId == q.Id).SelectMany(o => AnalysisResponseItems.Parse(o.OutputJson)).ToList(),
            firstVersions.FirstOrDefault(v => v.ArtifactId == q.Id) is { } first ? QuestionOf(first.DataSnapshotJson) : null));
    }

    private static string? QuestionOf(string dataJson)
    {
        try
        {
            return JsonSerializer.Deserialize<ClarificationQuestionPayload>(dataJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Question;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
