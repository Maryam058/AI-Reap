using System.Text;
using AiReap.Application.Artifacts;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiReap.Application.Ai.Pipeline;

// Shared plumbing for the generation services (Business Rules, Design, Database, API,
// Implementation Tasks, Test Cases) — each follows the same shape: load source, load some
// upstream artifacts for context, call the AI, create new artifacts + v1 versions + relationship
// links, log an AIExecution, save. Extracted here once a fourth/fifth near-identical copy of
// this plumbing would otherwise have appeared (RequirementGenerationService and
// UserStoryService predate this and are left as-is rather than churned for consistency alone).
public static class GenerationSupport
{
    public static async Task<RequirementSource> LoadSourceAsync(IAiReapDbContext db, Guid requirementSourceId, CancellationToken cancellationToken)
    {
        return await db.RequirementSources.FirstOrDefaultAsync(s => s.Id == requirementSourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"RequirementSource {requirementSourceId} not found.");
    }

    public static async Task<List<Artifact>> LoadArtifactsAsync(
        IAiReapDbContext db, Guid requirementSourceId, ArtifactType type, CancellationToken cancellationToken)
    {
        return await db.Artifacts
            .Where(a => a.RequirementSourceId == requirementSourceId && a.ArtifactType == type)
            .OrderBy(a => a.Code)
            .ToListAsync(cancellationToken);
    }

    public static string BuildContextBlock(string heading, IEnumerable<Artifact> artifacts)
    {
        var list = artifacts.ToList();
        if (list.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder($"\n\n{heading}\n");
        foreach (var a in list)
        {
            sb.Append($"- {a.Code}: {a.Title}\n");
        }

        return sb.ToString();
    }

    public static Artifact NewArtifact(
        RequirementSource source, ArtifactType type, string code, string title,
        ArtifactPriority? priority, string dataJson, string userId, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = source.ProjectId,
        ArtifactType = type,
        Code = code,
        Title = title,
        Priority = priority,
        Status = ArtifactStatus.AiGenerated,
        Origin = ArtifactOrigin.Ai,
        RequirementSourceId = source.Id,
        DataJson = dataJson,
        CurrentVersion = 1,
        CreatedByUserId = userId,
        CreatedAt = now,
        UpdatedAt = now
    };

    public static ArtifactVersion InitialVersion(Artifact artifact, string reason, string userId, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        ArtifactId = artifact.Id,
        VersionNumber = 1,
        DataSnapshotJson = artifact.DataJson,
        Title = artifact.Title,
        Priority = artifact.Priority,
        Status = artifact.Status,
        ChangedByUserId = userId,
        ChangedAt = now,
        Reason = reason,
        Origin = ArtifactOrigin.Ai
    };

    // Every pipeline service follows the same call-then-parse shape, so the LLM call and the
    // strict-JSON validation of its response - the two places a production failure actually
    // needs debugging context to diagnose - are logged once, here, instead of ad hoc per service.
    //
    // Transient provider failures are retried inside the provider client (ResilientAiChatClient).
    // Here, a response that arrives but fails shape/semantic validation gets exactly one repair
    // attempt with the validation errors fed back; if that also fails, the whole operation is
    // rejected (nothing is persisted). knownArtifactCodes, when given, is the set of artifact codes
    // the model was shown - references to anything else are rejected.
    public static async Task<(string RawResponse, T Parsed)> CallAiAndParseAsync<T>(
        IAiChatClient chatClient, ILogger logger, string operationType, string inputReference,
        string systemPrompt, string userPrompt, CancellationToken cancellationToken,
        IEnumerable<string>? knownArtifactCodes = null, IEnumerable<string>? knownSourceReferences = null) where T : class
    {
        var knownCodes = knownArtifactCodes?.ToList();
        var knownRefs = knownSourceReferences?.ToList();
        var prompt = userPrompt;

        for (var attempt = 1; ; attempt++)
        {
            var rawResponse = await CallAiAsync(chatClient, logger, operationType, inputReference, systemPrompt, prompt, cancellationToken);

            try
            {
                return (rawResponse, AiJsonParser.Parse<T>(rawResponse, knownCodes, knownRefs));
            }
            catch (AiOutputValidationException ex) when (attempt == 1 && ex.Repairable)
            {
                // Log the problems, not the response body: it may quote user-supplied requirement text.
                logger.LogWarning(
                    "AI response rejected, requesting one repair: {OperationType} input={InputReference} errors={Errors}",
                    operationType, inputReference, string.Join(" | ", ex.Errors));
                prompt = userPrompt + BuildRepairInstruction(ex.Errors);
            }
            catch (AiOutputValidationException ex)
            {
                logger.LogError(
                    "AI response failed validation: {OperationType} input={InputReference} attempt={Attempt} errors={Errors}",
                    operationType, inputReference, attempt, string.Join(" | ", ex.Errors));
                throw;
            }
        }
    }

    private static async Task<string> CallAiAsync(
        IAiChatClient chatClient, ILogger logger, string operationType, string inputReference,
        string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "AI call starting: {OperationType} model={Model} input={InputReference}",
            operationType, chatClient.ModelName, inputReference);

        string rawResponse;
        try
        {
            rawResponse = await chatClient.CompleteAsync(systemPrompt, userPrompt, cancellationToken);
        }
        catch (AiProviderException ex)
        {
            // Expected, classified failure - message only (it is already sanitized), no stack trace noise.
            logger.LogError(
                "AI call failed: {OperationType} model={Model} input={InputReference} kind={Kind} status={Status} message={Message}",
                operationType, chatClient.ModelName, inputReference, ex.Kind, (int?)ex.ProviderStatusCode, ex.Message);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "AI call failed: {OperationType} model={Model} input={InputReference}",
                operationType, chatClient.ModelName, inputReference);
            throw;
        }

        logger.LogInformation(
            "AI call succeeded: {OperationType} input={InputReference} responseLength={ResponseLength}",
            operationType, inputReference, rawResponse.Length);
        return rawResponse;
    }

    private static string BuildRepairInstruction(IReadOnlyList<string> errors) =>
        "\n\n---\nYour previous response was rejected by automatic validation for these reasons:\n- " +
        string.Join("\n- ", errors) +
        "\nReturn a corrected response: ONLY the single JSON object in the required shape, with every problem above fixed. " +
        "Do not invent facts to fill gaps - leave optional fields empty instead.";

    public static void LinkToRelated(IAiReapDbContext db, Artifact newArtifact, IEnumerable<Artifact> related, RelationshipType type, DateTime now)
    {
        foreach (var target in related)
        {
            db.ArtifactRelationships.Add(new ArtifactRelationship
            {
                Id = Guid.NewGuid(),
                SourceArtifactId = newArtifact.Id,
                TargetArtifactId = target.Id,
                RelationshipType = type,
                CreatedAt = now
            });
        }
    }

    private static AIExecution NewExecution(
        Guid projectId, string operationType, string promptTemplateVersion, string userId, string model,
        string inputReference, string rawResponse, DateTime now, Guid? producedArtifactId) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        OperationType = operationType,
        PromptTemplateVersion = promptTemplateVersion,
        UserId = userId,
        Timestamp = now,
        Model = model,
        InputReference = inputReference,
        OutputJson = rawResponse,
        ProducedArtifactId = producedArtifactId,
        Accepted = null
    };

    // §27 — one AIExecution row per produced artifact, so ProducedArtifactId and the
    // Accepted/Rejected decision (set later in ArtifactService.UpdateStatusAsync) can be
    // tracked per artifact rather than only for "the first one" when a single AI call produces
    // several (e.g. RequirementGenerationService's FR+NFR batch, or a multi-item design/task
    // list). All rows for one call share the same OutputJson - they represent one underlying AI
    // call, fanned out per resulting artifact for audit/review purposes. If the call produced no
    // artifacts (e.g. analysis found no missing information), exactly one row is still logged,
    // with no ProducedArtifactId, so the call itself remains auditable.
    public static void AddExecutions(
        IAiReapDbContext db, Guid projectId, string operationType, string promptTemplateVersion,
        string userId, string model, string inputReference, string rawResponse, DateTime now,
        IReadOnlyList<Guid> producedArtifactIds)
    {
        if (producedArtifactIds.Count == 0)
        {
            db.AIExecutions.Add(NewExecution(projectId, operationType, promptTemplateVersion, userId, model, inputReference, rawResponse, now, null));
            return;
        }

        foreach (var artifactId in producedArtifactIds)
        {
            db.AIExecutions.Add(NewExecution(projectId, operationType, promptTemplateVersion, userId, model, inputReference, rawResponse, now, artifactId));
        }
    }
}
