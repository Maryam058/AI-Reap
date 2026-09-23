using AiReap.Application.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Audit;

public class AuditTrailService : IAuditTrailService
{
    private readonly IAiReapDbContext _db;

    public AuditTrailService(IAiReapDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<AiExecutionResponse>> GetForProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var executions = await _db.AIExecutions
            .Where(e => e.ProjectId == projectId)
            .OrderByDescending(e => e.Timestamp)
            .Take(200)
            .ToListAsync(cancellationToken);

        return executions.Select(e => new AiExecutionResponse(
            e.Id, e.OperationType, e.UserId, e.Timestamp, e.Model, e.PromptTemplateVersion, e.InputReference, e.OutputJson, e.Accepted, e.ProducedArtifactId)).ToList();
    }
}
