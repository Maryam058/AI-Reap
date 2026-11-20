using System.Text.Json;
using AiReap.Application.Artifacts;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Audit;

public class AuditTrailService : IAuditTrailService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;

    public AuditTrailService(IAiReapDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<AiExecutionResponse>> GetForProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var query = _db.AIExecutions.Where(e => e.ProjectId == projectId);

        // A caller limited by ArtifactVisibility (a Developer) sees only executions whose produced
        // artifact they can see.
        var restricted = _currentUser.SeesOnlyApprovedArtifacts();
        if (restricted)
        {
            var visibleIds = _db.Artifacts.VisibleTo(_currentUser).Select(a => a.Id);
            query = query.Where(e => e.ProducedArtifactId != null && visibleIds.Contains(e.ProducedArtifactId.Value));
        }

        var executions = await query
            .OrderByDescending(e => e.Timestamp)
            .Take(200)
            .ToListAsync(cancellationToken);

        return restricted
            ? await IsolateAsync(executions, cancellationToken)
            : executions.Select(e => ToResponse(e, e.InputReference, e.OutputJson)).ToList();
    }

    // §4/§27 - one AI call that produced several artifacts is logged as one row per artifact, and
    // every row stores the call's whole raw response (GenerationSupport.AddExecutions): the other
    // artifacts of the batch - possibly unapproved requirements - are in it. A restricted caller
    // therefore never gets the raw response. Each row's output is replaced by that row's own
    // artifact as the AI produced it (its first, AI-origin version: code, type, title, data) -
    // content the caller can already read through GET /api/artifacts/{id}/versions. An input
    // reference is withheld unless it is the id of an artifact they may see - so neither a hidden
    // artifact (e.g. the user story acceptance criteria were generated from) nor a raw requirement
    // source (S2) is pointed to. The stored row is never changed, so
    // privileged roles keep the full raw response.
    private async Task<IReadOnlyList<AiExecutionResponse>> IsolateAsync(List<AIExecution> executions, CancellationToken cancellationToken)
    {
        var producedIds = executions.Select(e => e.ProducedArtifactId!.Value).Distinct().ToList();
        var inputIds = executions
            .Select(e => Guid.TryParse(e.InputReference, out var id) ? id : (Guid?)null)
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var lookupIds = producedIds.Concat(inputIds).Distinct().ToList();

        var artifacts = await _db.Artifacts.AsNoTracking()
            .Where(a => lookupIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, cancellationToken);
        var firstVersions = await _db.ArtifactVersions.AsNoTracking()
            .Where(v => producedIds.Contains(v.ArtifactId)
                        && v.VersionNumber == _db.ArtifactVersions.Where(x => x.ArtifactId == v.ArtifactId).Min(x => x.VersionNumber))
            .ToDictionaryAsync(v => v.ArtifactId, cancellationToken);

        // Kept only when it names an artifact the caller can see. Otherwise it is a requirement
        // source id (S2 - raw sources are not theirs to follow), a project id, or a hidden artifact.
        string? InputFor(AIExecution e) =>
            Guid.TryParse(e.InputReference, out var id) && artifacts.TryGetValue(id, out var input) && _currentUser.CanSee(input)
                ? e.InputReference
                : null;

        string OutputFor(AIExecution e)
        {
            var id = e.ProducedArtifactId!.Value;
            if (!artifacts.TryGetValue(id, out var artifact))
            {
                return "{}";
            }

            firstVersions.TryGetValue(id, out var first);
            return JsonSerializer.Serialize(new
            {
                scope = "artifact",
                note = "Only this artifact's part of the AI output is shown.",
                artifactCode = artifact.Code,
                artifactType = artifact.ArtifactType.ToString(),
                title = first?.Title ?? artifact.Title,
                // S2/P0-2 — requirement evidence without its quote text (RequirementEvidenceFields).
                data = ArtifactJson.ToCamelCaseElement(RequirementEvidenceFields.RedactForRestrictedCaller(first?.DataSnapshotJson ?? artifact.DataJson))
            }, JsonOptions);
        }

        return executions.Select(e => ToResponse(e, InputFor(e), OutputFor(e))).ToList();
    }

    private static AiExecutionResponse ToResponse(AIExecution e, string? inputReference, string outputJson) => new(
        e.Id, e.OperationType, e.UserId, e.Timestamp, e.Model, e.PromptTemplateVersion, inputReference, outputJson, e.Accepted, e.ProducedArtifactId);
}
