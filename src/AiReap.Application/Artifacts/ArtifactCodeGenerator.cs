using AiReap.Application.Persistence;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Artifacts;

// Human-facing IDs (FR-001, US-014, ...) unique per project+type — see the unique index
// on (ProjectId, ArtifactType, Code) in AiReapDbContext.
public static class ArtifactCodeGenerator
{
    private static readonly Dictionary<ArtifactType, string> Prefixes = new()
    {
        [ArtifactType.FunctionalRequirement] = "FR",
        [ArtifactType.NonFunctionalRequirement] = "NFR",
        [ArtifactType.BusinessRule] = "BR",
        [ArtifactType.UserStory] = "US",
        [ArtifactType.AcceptanceCriterion] = "AC",
        [ArtifactType.ClarificationQuestion] = "CQ",
        [ArtifactType.DesignArtifact] = "DA",
        [ArtifactType.ApiSpecification] = "API",
        [ArtifactType.DataEntity] = "DE",
        [ArtifactType.ImplementationTask] = "TASK",
        [ArtifactType.TestCase] = "TC"
    };

    public static async Task<List<string>> ReserveCodesAsync(
        IAiReapDbContext db, Guid projectId, ArtifactType type, int count, CancellationToken cancellationToken)
    {
        var prefix = Prefixes[type];

        var existingCodes = await db.Artifacts
            .Where(a => a.ProjectId == projectId && a.ArtifactType == type)
            .Select(a => a.Code)
            .ToListAsync(cancellationToken);

        var maxExisting = existingCodes
            .Select(code => ParseSuffix(code, prefix))
            .DefaultIfEmpty(0)
            .Max();

        var codes = new List<string>(count);
        for (var i = 1; i <= count; i++)
        {
            codes.Add($"{prefix}-{maxExisting + i:D3}");
        }

        return codes;
    }

    private static int ParseSuffix(string code, string prefix)
    {
        var expectedStart = prefix + "-";
        if (!code.StartsWith(expectedStart, StringComparison.Ordinal))
        {
            return 0;
        }

        return int.TryParse(code[expectedStart.Length..], out var number) ? number : 0;
    }
}
