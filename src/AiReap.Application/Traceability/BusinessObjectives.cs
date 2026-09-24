using System.Text.Json;
using System.Text.RegularExpressions;
using AiReap.Application.Artifacts;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Traceability;

// §21 — Business Objective is the head of the traceability chain. Objectives are written by people
// in the project's Objectives field (one per line or bullet); each becomes a BO-xxx artifact
// (Origin Human) so requirements can link to it like any other artifact. Existing objectives are
// never deleted or renamed by a re-sync: removing a line from the project leaves its history intact.
public static partial class BusinessObjectives
{
    public static IReadOnlyList<string> Split(string? objectivesText)
    {
        if (string.IsNullOrWhiteSpace(objectivesText))
        {
            return [];
        }

        return objectivesText
            .Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => BulletPrefix().Replace(line, "").Trim())
            .Where(line => line.Length > 0)
            .Select(line => line.Length <= 300 ? line : line[..300])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<List<Artifact>> SyncAsync(
        IAiReapDbContext db, Guid projectId, string? objectivesText, string userId, CancellationToken cancellationToken)
    {
        var existing = await db.Artifacts
            .Where(a => a.ProjectId == projectId && a.ArtifactType == ArtifactType.BusinessObjective)
            .OrderBy(a => a.Code)
            .ToListAsync(cancellationToken);

        var missing = Split(objectivesText)
            .Where(line => !existing.Any(a => string.Equals(a.Title, line, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (missing.Count == 0)
        {
            return existing;
        }

        var codes = await ArtifactCodeGenerator.ReserveCodesAsync(db, projectId, ArtifactType.BusinessObjective, missing.Count, cancellationToken);
        var now = DateTime.UtcNow;
        for (var i = 0; i < missing.Count; i++)
        {
            var artifact = new Artifact
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                ArtifactType = ArtifactType.BusinessObjective,
                Code = codes[i],
                Title = missing[i],
                Status = ArtifactStatus.Draft,
                Origin = ArtifactOrigin.Human,
                DataJson = JsonSerializer.Serialize(new { statement = missing[i], source = "Project objectives" }),
                CurrentVersion = 1,
                CreatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Artifacts.Add(artifact);
            db.ArtifactVersions.Add(new ArtifactVersion
            {
                Id = Guid.NewGuid(),
                ArtifactId = artifact.Id,
                VersionNumber = 1,
                DataSnapshotJson = artifact.DataJson,
                Title = artifact.Title,
                Status = artifact.Status,
                ChangedByUserId = userId,
                ChangedAt = now,
                Reason = "Recorded from the project's objectives",
                Origin = ArtifactOrigin.Human
            });
            existing.Add(artifact);
        }

        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    [GeneratedRegex(@"^\s*(?:[-*•·]|\d+[.)])\s*")]
    private static partial Regex BulletPrefix();
}
