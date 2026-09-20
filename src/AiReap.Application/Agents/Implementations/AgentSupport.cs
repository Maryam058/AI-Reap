using System.Text.Json;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Persistence;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Agents.Implementations;

// Small helpers shared by the six agents. The agents themselves are thin orchestration over
// the Phase 1–3 services — no new AI prompts except the Review Agent's narrative.
internal static class AgentSupport
{
    public static readonly IReadOnlyList<string> AdminOnly = new[] { Roles.Administrator };

    public static IReadOnlyList<string> AdminAnd(string role) => new[] { Roles.Administrator, role };

    public static AgentArtifactRef ToRef(Artifact a) => new(a.Id, a.Code, a.ArtifactType.ToString(), a.Title);

    public static AgentArtifactRef ToRef(ArtifactResponse a) => new(a.Id, a.Code, a.ArtifactType.ToString(), a.Title);

    public static Task<List<Artifact>> LoadAsync(IAiReapDbContext db, Guid sourceId, ArtifactType type, CancellationToken ct) =>
        GenerationSupport.LoadArtifactsAsync(db, sourceId, type, ct);

    public static Task<bool> ExistsAsync(IAiReapDbContext db, Guid sourceId, ArtifactType type, CancellationToken ct) =>
        db.Artifacts.AnyAsync(a => a.RequirementSourceId == sourceId && a.ArtifactType == type, ct);

    public static int OpenClarifications(IEnumerable<Artifact> questions) =>
        questions.Count(q =>
            (JsonSerializer.Deserialize<ClarificationQuestionPayload>(q.DataJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?.ClarificationStatus ?? "Open") == "Open");

    // Both helpers answer "which artifacts already have X", so a re-run skips finished work:
    // TargetsOfAsync = ids on the target end of `type` links whose source is a `sourceType`
    // (e.g. stories that have acceptance criteria); SourcesOfAsync = ids on the source end of
    // links whose target is a `targetType` (e.g. FRs that have test cases).
    public static async Task<HashSet<Guid>> TargetsOfAsync(
        IAiReapDbContext db, ArtifactType sourceType, RelationshipType type, Guid projectId, CancellationToken ct) =>
        (await db.ArtifactRelationships
            .Where(r => r.RelationshipType == type
                        && r.SourceArtifact!.ArtifactType == sourceType
                        && r.SourceArtifact.ProjectId == projectId)
            .Select(r => r.TargetArtifactId)
            .ToListAsync(ct)).ToHashSet();

    public static async Task<HashSet<Guid>> SourcesOfAsync(
        IAiReapDbContext db, ArtifactType targetType, RelationshipType type, Guid projectId, CancellationToken ct) =>
        (await db.ArtifactRelationships
            .Where(r => r.RelationshipType == type
                        && r.TargetArtifact!.ArtifactType == targetType
                        && r.TargetArtifact.ProjectId == projectId)
            .Select(r => r.SourceArtifactId)
            .ToListAsync(ct)).ToHashSet();
}
