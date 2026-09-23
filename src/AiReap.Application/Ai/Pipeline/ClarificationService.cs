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

    public ClarificationService(IAiReapDbContext db, IProjectAccessService projectAccess)
    {
        _db = db;
        _projectAccess = projectAccess;
    }

    public async Task<IReadOnlyList<ArtifactResponse>> GetForRequirementSourceAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
    {
        var projectId = await _db.RequirementSources
            .Where(s => s.Id == requirementSourceId)
            .Select(s => (Guid?)s.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);
        if (projectId is not null)
        {
            await _projectAccess.EnsureMemberAsync(projectId.Value, cancellationToken);
        }

        var questions = await _db.Artifacts
            .Where(a => a.RequirementSourceId == requirementSourceId && a.ArtifactType == ArtifactType.ClarificationQuestion)
            .OrderBy(a => a.Code)
            .ToListAsync(cancellationToken);

        return questions.Select(ArtifactResponseMapper.ToResponse).ToList();
    }
}
