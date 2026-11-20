using AiReap.Application.Artifacts;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
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
            .Select(s => new { s.ProjectId })
            .FirstOrDefaultAsync(cancellationToken);
        if (source is not null)
        {
            await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);
        }

        var questions = await _db.Artifacts.VisibleTo(_currentUser)
            .Where(a => a.RequirementSourceId == requirementSourceId && a.ArtifactType == ArtifactType.ClarificationQuestion)
            .OrderBy(a => a.Code)
            .ToListAsync(cancellationToken);

        // Only questions whose source evidence is verified are returned (ClarificationVisibility), each
        // linked to its source as far as the source verifies; a question saved without located evidence
        // gets it recovered from the analysis response that raised it, when that is kept.
        var shown = await ClarificationVisibility.ShownAsync(_db, questions, cancellationToken);
        questions = questions.Where(q => shown.ContainsKey(q.Id)).ToList();

        // Source references point into the raw source, so a restricted caller (S2) gets none.
        if (!_currentUser.CanReadRawSources())
        {
            return questions.Select(q => ArtifactResponseMapper.ToResponse(q) with
            {
                Data = ArtifactJson.ToCamelCaseElement(RequirementEvidenceFields.RedactForRestrictedCaller(q.DataJson))
            }).ToList();
        }

        return questions.Select(q => ArtifactResponseMapper.ToResponse(q) with
        {
            Data = ArtifactJson.ToCamelCaseElement(shown[q.Id])
        }).ToList();
    }
}
