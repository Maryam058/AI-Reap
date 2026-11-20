using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Ai.Pipeline;

// While a batched run (AiChunkRunService) of an operation is active for a source, the normal
// Analyze / Generate of that operation is refused, so the two cannot interleave. Only checked when
// batch mode is on: with Ai:Groq:MaxBatchedRunParts = 0 the AiChunkRuns table is never queried.
public static class AiChunkRunGuard
{
    public static async Task EnsureNoActiveRunAsync(
        IAiReapDbContext db, IAiBatchSettings settings, Guid requirementSourceId, AiChunkRunOperation operation,
        CancellationToken cancellationToken)
    {
        if (!settings.BatchModeEnabled())
        {
            return;
        }

        var active = await db.AiChunkRuns.AsNoTracking().AnyAsync(r =>
            r.RequirementSourceId == requirementSourceId && r.Operation == operation
            && (r.Status == AiChunkRunStatus.Running || r.Status == AiChunkRunStatus.Staged), cancellationToken);
        if (active)
        {
            throw new ConflictException(
                $"A batched {(operation == AiChunkRunOperation.Analyze ? "analysis" : "requirement generation")} is in progress for this " +
                "requirement source. Continue and save it, or abandon it, before running it again.");
        }
    }
}
