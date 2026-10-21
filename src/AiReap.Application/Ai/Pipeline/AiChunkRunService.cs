using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiReap.Application.Artifacts;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiReap.Application.Ai.Pipeline;

public interface IAiChunkRunService
{
    Task<AiChunkRunResponse> StartAsync(Guid requirementSourceId, AiChunkRunOperation operation, CancellationToken cancellationToken = default);
    Task<AiChunkRunResponse> ContinueAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<AiChunkRunResponse> CommitAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<AiChunkRunResponse> AbandonAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<AiChunkRunResponse> GetAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<AiChunkRunResponse?> GetActiveAsync(Guid requirementSourceId, AiChunkRunOperation operation, CancellationToken cancellationToken = default);
}

// Batch mode (Ai:Groq:MaxBatchedRunParts > 0): one requirement source - one logical document, never
// split into other sources or projects - analysed or turned into requirements in internally bounded
// batches when it is too large for a provider's per-request budget (Groq).
//
//   Start     plans the source with ChunkPlanner for the smallest per-request budget among the
//             configured providers and freezes: source and context fingerprints, the plan, and
//             each part's exact prompt. No chat request.
//   Continue  sends at most MaxChunkedParts pending parts (one action), saving each part as soon as
//             it succeeds; a quota or outage error stops the action and keeps every finished part;
//             a succeeded part is never sent again. One Continue at a time per run (lease).
//   Commit    merges the parts and creates every artifact in one save, exactly as the normal path
//             (IChunkRunOperation.CommitAsync), and marks the run Committed in the same save.
//   Abandon   ends the run.
// Continue and Commit refuse (and supersede the run) if the source text or its context changed
// since Start. Nothing becomes an artifact before Commit. The provider routing is unchanged: every
// part goes through the configured IAiChatClient (primary first, fallback after a rate limit etc.).
public class AiChunkRunService : IAiChunkRunService
{
    public const int MaxPartAttempts = 3;
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);

    private readonly IAiReapDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly ICurrentUser _currentUser;
    private readonly IAiBatchSettings _settings;
    private readonly IAiRouteInspector _routeInspector;
    private readonly IEnumerable<IChunkRunOperation> _operations;
    private readonly ILogger<AiChunkRunService> _logger;

    public AiChunkRunService(
        IAiReapDbContext db, IProjectAccessService projectAccess, ICurrentUser currentUser, IAiBatchSettings settings,
        IAiRouteInspector routeInspector, IEnumerable<IChunkRunOperation> operations, ILogger<AiChunkRunService> logger)
    {
        _db = db;
        _projectAccess = projectAccess;
        _currentUser = currentUser;
        _settings = settings;
        _routeInspector = routeInspector;
        _operations = operations;
        _logger = logger;
    }

    // Frozen at Start (AiChunkRun.PlanJson).
    private sealed record PlanData(
        string BudgetProvider, int InputBudget, int MaxPartSourceTokens,
        List<string> KnownCodes, List<string> KnownReferences, bool ValidateSourceIds, List<string> SourceIds);

    // ---- Start -------------------------------------------------------------------------------

    public async Task<AiChunkRunResponse> StartAsync(Guid requirementSourceId, AiChunkRunOperation operation, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var source = await _db.RequirementSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == requirementSourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"RequirementSource {requirementSourceId} not found.");
        await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);

        if (await ActiveRunIdAsync(source.Id, operation, cancellationToken) is { } activeId)
        {
            throw new AiChunkRunActiveException(activeId);
        }

        var budget = BudgetProvider(_routeInspector.Inspect())
            ?? throw new AiBatchRequestException(AiBatchRequestException.NotNeeded,
                "No configured AI provider has a per-request size limit, so this source never needs to be processed in batches. Run it normally.");

        var op = Operation(operation);
        var inputs = await op.BuildRunInputsAsync(source, cancellationToken);
        var plan = ChunkPlanner.Plan(inputs.SystemPrompt, inputs.SourceText, inputs.FixedContext, inputs.Shared, budget.InputBudget, maxParts: 0);
        switch (plan.Outcome)
        {
            case ChunkPlanOutcome.Whole:
                throw new AiBatchRequestException(AiBatchRequestException.NotNeeded,
                    $"The source fits in one {budget.Provider} request, so it does not need to be processed in batches. Run it normally.");
            case ChunkPlanOutcome.CannotSplit:
                throw new AiBatchRequestException(AiBatchRequestException.CannotPlan,
                    $"{budget.Provider}'s {budget.InputBudget:N0}-token input budget leaves too little room for source text next to the " +
                    $"~{plan.ReservedTokensPerPart:N0} tokens every part must carry.");
            case ChunkPlanOutcome.SharedContextDoesNotFit:
                throw new AiBatchRequestException(AiBatchRequestException.CannotPlan,
                    $"{plan.UnplacedSharedItems:N0} of the {plan.SharedContextItems:N0} clarifications would not fit in any part for {budget.Provider}.");
        }

        if (plan.Chunks.Count > _settings.MaxBatchedRunParts)
        {
            throw new AiBatchRequestException(AiBatchRequestException.TooManyParts,
                $"This source would need {plan.Chunks.Count} parts for {budget.Provider}; a batched run may have at most " +
                $"{_settings.MaxBatchedRunParts} (Ai:Groq:MaxBatchedRunParts={_settings.MaxBatchedRunParts}).");
        }

        var now = DateTime.UtcNow;
        var anchors = SourceChunker.SectionAnchors(plan.Chunks);
        var planData = new PlanData(budget.Provider, budget.InputBudget!.Value, plan.MaxPartSourceTokens,
            inputs.KnownCodes.ToList(), inputs.KnownReferences.ToList(), inputs.ValidateSourceIds,
            SrsIdExtractor.Extract(inputs.SourceText).ToList());
        var chunkHashes = plan.Chunks.Select(Sha256).ToList();
        var run = new AiChunkRun
        {
            Id = Guid.NewGuid(),
            ProjectId = source.ProjectId,
            RequirementSourceId = source.Id,
            Operation = operation,
            Status = AiChunkRunStatus.Running,
            PromptTemplateVersion = inputs.PromptTemplateVersion,
            SystemPrompt = inputs.SystemPrompt,
            SourceHash = Sha256(source.RawText),
            ContextHash = Sha256(inputs.ContextMaterial),
            PlanHash = Sha256($"{budget.Provider}|{budget.InputBudget}|{plan.MaxPartSourceTokens}|{string.Join(",", chunkHashes)}"),
            FixedContext = inputs.FixedContext,
            PlanJson = JsonSerializer.Serialize(planData),
            PartCount = plan.Chunks.Count,
            StartedByUserId = _currentUser.UserId,
            StartedAt = now,
            UpdatedAt = now,
            Parts = plan.Chunks.Select((chunk, i) => new AiChunkRunPart
            {
                Id = Guid.NewGuid(),
                PartNumber = i + 1,
                ChunkHash = chunkHashes[i],
                SectionPath = anchors[i] is { } a && a.Length > 500 ? a[..500] : anchors[i],
                SourceIdsJson = JsonSerializer.Serialize(SrsIdExtractor.Extract(chunk)),
                PromptText = GenerationSupport.PartPrompt(i + 1, plan.Chunks.Count, chunk, plan.SharedContextParts[i], inputs.FixedContext),
                Status = AiChunkRunPartStatus.Pending
            }).ToList()
        };

        _db.AiChunkRuns.Add(run);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ArtifactPersistence.IsRetryableSaveRace(ex))
        {
            // Lost to a concurrent Start: the one-active-run index rejected this run (or the two
            // inserts deadlocked). Nothing of this run was saved.
            ArtifactPersistence.Undo(_db);
            if (await ActiveRunIdAsync(source.Id, operation, cancellationToken) is { } winner)
            {
                throw new AiChunkRunActiveException(winner);
            }

            throw new ConflictException("Another batched run was being started for this source at the same time. Try again.");
        }

        _logger.LogInformation(
            "Batched {Operation} run {RunId} started for source {SourceId}: {Parts} parts for {Provider}'s {Budget}-token input budget.",
            operation, run.Id, source.Id, run.PartCount, budget.Provider, budget.InputBudget);
        return Respond(run, run.Parts);
    }

    // ---- Continue ----------------------------------------------------------------------------

    public async Task<AiChunkRunResponse> ContinueAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var (run, parts, source) = await LoadAsync(runId, cancellationToken);

        if (run.Status == AiChunkRunStatus.Staged)
        {
            return Respond(run, parts);
        }

        EnsureStatus(run, AiChunkRunStatus.Running);
        await EnsureCurrentAsync(run, parts, source, cancellationToken);

        // Take the lease: a concurrent Continue either sees it held, or loses the rowversion race.
        var now = DateTime.UtcNow;
        if (run.LeaseId is not null && run.LeaseExpiresAt > now)
        {
            throw new ConflictException("This batched run is being processed by another request. Wait for it to finish, then reload.");
        }

        var lease = Guid.NewGuid();
        run.LeaseId = lease;
        run.LeaseExpiresAt = now + LeaseDuration;
        run.UpdatedAt = now;
        await SaveRunAsync("This batched run was just changed by another request. Reload to see its current state.", cancellationToken);

        var op = Operation(run.Operation);
        var frozen = Frozen(run, source);
        var perAction = PartsPerAction();
        try
        {
            foreach (var part in parts.Where(p => p.Status != AiChunkRunPartStatus.Succeeded).OrderBy(p => p.PartNumber).Take(perAction).ToList())
            {
                if (!await SendAsync(run, part, op, frozen, source.Id, cancellationToken))
                {
                    break;
                }
            }

            if (run.Status == AiChunkRunStatus.Running && parts.All(p => p.Status == AiChunkRunPartStatus.Succeeded))
            {
                run.Status = AiChunkRunStatus.Staged;
                run.LastError = null;
            }
        }
        finally
        {
            if (run.LeaseId == lease)
            {
                run.LeaseId = null;
                run.LeaseExpiresAt = null;
                run.UpdatedAt = DateTime.UtcNow;
                try
                {
                    await _db.SaveChangesAsync(CancellationToken.None);
                }
                catch (DbUpdateException ex)
                {
                    // The lease expires on its own; the run state already saved per part is intact.
                    _logger.LogWarning(ex, "Could not release the lease of batched run {RunId}.", run.Id);
                    ArtifactPersistence.Undo(_db);
                }
            }
        }

        return Respond(run, parts);
    }

    // One part: saved as soon as it is known. Returns whether the action may go on to the next part.
    private async Task<bool> SendAsync(
        AiChunkRun run, AiChunkRunPart part, IChunkRunOperation op, ChunkRunFrozen frozen, Guid sourceId, CancellationToken cancellationToken)
    {
        var partIds = JsonSerializer.Deserialize<List<string>>(part.SourceIdsJson) ?? [];
        var proceed = true;
        try
        {
            var result = await op.SendPartAsync(
                new ChunkRunPartRequest(frozen, sourceId, part.PartNumber, run.PartCount, part.PromptText!, partIds, Delay: null),
                cancellationToken);
            part.Status = AiChunkRunPartStatus.Succeeded;
            part.Attempts++;
            part.RawResponse = result.RawResponse;
            part.ParsedJson = result.ParsedJson;
            part.Model = result.Model;
            part.LastError = null;
            part.CompletedAt = DateTime.UtcNow;
            run.LastError = null;
        }
        catch (AiProviderException ex) when (ex.IsTransient || ex.Kind is AiFailureKind.NotConfigured or AiFailureKind.Authentication)
        {
            // Quota, rate limit, outage, timeout - or a configuration problem an operator can fix:
            // retrying later can succeed, so it does not count as an attempt.
            Fail(run, part, ex.Message, terminal: false);
            proceed = false;
        }
        catch (AiProviderException ex) when (ex.Kind == AiFailureKind.RequestRejected)
        {
            // Too large or otherwise refused as sent: the same request cannot succeed.
            part.Attempts++;
            Fail(run, part, ex.Message, terminal: true);
            proceed = false;
        }
        catch (Exception ex) when (ex is AiOutputValidationException or AiProviderException)
        {
            // Invalid output (after its one repair), or blocked content: may succeed on a retry.
            part.Attempts++;
            Fail(run, part, ex.Message, terminal: part.Attempts >= MaxPartAttempts);
            proceed = false;
        }

        run.UpdatedAt = DateTime.UtcNow;
        if (run.LeaseId is not null)
        {
            run.LeaseExpiresAt = run.UpdatedAt + LeaseDuration;
        }

        await SaveRunAsync("This batched run was just changed by another request. Reload to see its current state.", cancellationToken);
        return proceed;
    }

    private void Fail(AiChunkRun run, AiChunkRunPart part, string message, bool terminal)
    {
        var text = Truncate(message.Replace(" Nothing was saved.", string.Empty), 2000);
        part.Status = AiChunkRunPartStatus.Failed;
        part.LastError = text;
        run.LastError = text;
        if (terminal)
        {
            run.Status = AiChunkRunStatus.Failed;
            run.CompletedAt = DateTime.UtcNow;
            ClearWorkingState(run.Parts);
        }

        _logger.LogWarning("Batched run {RunId} part {Part} of {Count} failed ({Terminal}): {Message}",
            run.Id, part.PartNumber, run.PartCount, terminal ? "run failed" : "retryable", text);
    }

    // ---- Commit ------------------------------------------------------------------------------

    public async Task<AiChunkRunResponse> CommitAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var (run, parts, source) = await LoadAsync(runId, cancellationToken);
        if (run.Status == AiChunkRunStatus.Committed)
        {
            return Respond(run, parts);
        }

        EnsureStatus(run, AiChunkRunStatus.Staged);
        if (run.LeaseId is not null && run.LeaseExpiresAt > DateTime.UtcNow)
        {
            throw new ConflictException("This batched run is being processed by another request. Wait for it to finish, then reload.");
        }

        await EnsureCurrentAsync(run, parts, source, cancellationToken);

        var outputs = parts.OrderBy(p => p.PartNumber)
            .Select(p => new ChunkRunPartOutput(p.PartNumber, run.PartCount, p.RawResponse ?? string.Empty, p.ParsedJson ?? "{}",
                p.Model ?? string.Empty, p.SectionPath))
            .ToList();
        ChunkRunCommitSummary committed;
        try
        {
            committed = await Operation(run.Operation).CommitAsync(source, Frozen(run, source), outputs, summary =>
            {
                // Inside the save's preparation: the run is marked Committed in the same SaveChanges
                // as the artifacts, guarded by its rowversion.
                var now = DateTime.UtcNow;
                run.Status = AiChunkRunStatus.Committed;
                run.ResultJson = JsonSerializer.Serialize(new AiChunkRunResult(summary.CreatedArtifactIds, summary.SkippedDuplicates));
                run.CompletedAt = now;
                run.UpdatedAt = now;
                run.LastError = null;
                ClearWorkingState(parts);
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another Commit (or Abandon) won. Nothing from this one was saved.
            var current = await _db.AiChunkRuns.AsNoTracking().FirstAsync(r => r.Id == runId, cancellationToken);
            if (current.Status == AiChunkRunStatus.Committed)
            {
                var currentParts = await _db.AiChunkRunParts.AsNoTracking().Where(p => p.AiChunkRunId == runId).ToListAsync(cancellationToken);
                return Respond(current, currentParts);
            }

            throw new ConflictException("This batched run was just changed by another request. Reload to see its current state.");
        }
        catch (AiOutputValidationException ex)
        {
            // The merged result is unusable (e.g. no functional requirement in any part).
            run.Status = AiChunkRunStatus.Failed;
            run.LastError = Truncate(ex.Message, 2000);
            run.CompletedAt = run.UpdatedAt = DateTime.UtcNow;
            ClearWorkingState(parts);
            await SaveRunAsync("This batched run was just changed by another request. Reload to see its current state.", cancellationToken);
            throw;
        }

        _logger.LogInformation("Batched {Operation} run {RunId} committed.", run.Operation, run.Id);

        // P0-4 — the coverage computed after the save (generation only) goes on this response; the
        // stored result stays as recorded inside the save.
        var response = Respond(run, parts);
        return committed.SourceIdCoverage is { } coverage && response.Result is { } result
            ? response with { Result = result with { SourceIdCoverage = coverage } }
            : response;
    }

    // ---- Abandon / read ----------------------------------------------------------------------

    public async Task<AiChunkRunResponse> AbandonAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var (run, parts, _) = await LoadAsync(runId, cancellationToken);
        if (run.Status is AiChunkRunStatus.Abandoned or AiChunkRunStatus.Superseded or AiChunkRunStatus.Failed)
        {
            return Respond(run, parts);
        }

        EnsureStatus(run, AiChunkRunStatus.Running, AiChunkRunStatus.Staged);
        if (run.LeaseId is not null && run.LeaseExpiresAt > DateTime.UtcNow)
        {
            throw new ConflictException("This batched run is being processed by another request. Wait for it to finish, then reload.");
        }

        run.Status = AiChunkRunStatus.Abandoned;
        run.CompletedAt = run.UpdatedAt = DateTime.UtcNow;
        ClearWorkingState(parts);
        await SaveRunAsync("This batched run was just changed by another request. Reload to see its current state.", cancellationToken);
        return Respond(run, parts);
    }

    public async Task<AiChunkRunResponse> GetAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var run = await _db.AiChunkRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            ?? throw new KeyNotFoundException($"AI chunk run {runId} not found.");
        await _projectAccess.EnsureMemberAsync(run.ProjectId, cancellationToken);
        var parts = await _db.AiChunkRunParts.AsNoTracking().Where(p => p.AiChunkRunId == runId).ToListAsync(cancellationToken);
        return Respond(run, parts);
    }

    public async Task<AiChunkRunResponse?> GetActiveAsync(Guid requirementSourceId, AiChunkRunOperation operation, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var source = await _db.RequirementSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == requirementSourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"RequirementSource {requirementSourceId} not found.");
        await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);
        return await ActiveRunIdAsync(requirementSourceId, operation, cancellationToken) is { } id
            ? await GetAsync(id, cancellationToken)
            : null;
    }

    // ---- helpers -----------------------------------------------------------------------------

    private void EnsureEnabled()
    {
        if (!_settings.BatchModeEnabled())
        {
            throw new AiBatchRequestException(AiBatchRequestException.BatchModeDisabled,
                "Batch mode is disabled (Ai:Groq:MaxBatchedRunParts=0). Analyze and Generate run normally.");
        }
    }

    private IChunkRunOperation Operation(AiChunkRunOperation operation) =>
        _operations.Single(o => o.Operation == operation);

    // The provider whose per-request budget the parts are sized to: the smallest budget among the
    // configured providers, so any of them can serve any part. Null when none has a budget.
    public static AiProviderLimits? BudgetProvider(AiRouteSnapshot route) =>
        new[] { route.Primary, route.Fallback }
            .Where(p => p is { LimitsKnown: true, InputBudget: not null })
            .OrderBy(p => p!.InputBudget)
            .FirstOrDefault();

    // Parts one Continue may send: the budget provider's MaxChunkedParts, or the run limit when
    // that check is off (0) or no budgeted provider is configured any more.
    private int PartsPerAction() =>
        BudgetProvider(_routeInspector.Inspect()) is { MaxParts: > 0 } budget ? budget.MaxParts : _settings.MaxBatchedRunParts;

    private async Task<Guid?> ActiveRunIdAsync(Guid sourceId, AiChunkRunOperation operation, CancellationToken cancellationToken) =>
        await _db.AiChunkRuns.AsNoTracking()
            .Where(r => r.RequirementSourceId == sourceId && r.Operation == operation
                && (r.Status == AiChunkRunStatus.Running || r.Status == AiChunkRunStatus.Staged))
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<(AiChunkRun Run, List<AiChunkRunPart> Parts, RequirementSource Source)> LoadAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await _db.AiChunkRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            ?? throw new KeyNotFoundException($"AI chunk run {runId} not found.");
        await _projectAccess.EnsureMemberAsync(run.ProjectId, cancellationToken);
        var parts = await _db.AiChunkRunParts.Where(p => p.AiChunkRunId == runId).OrderBy(p => p.PartNumber).ToListAsync(cancellationToken);
        var source = await _db.RequirementSources.AsNoTracking().FirstAsync(s => s.Id == run.RequirementSourceId, cancellationToken);
        return (run, parts, source);
    }

    private static void EnsureStatus(AiChunkRun run, params AiChunkRunStatus[] allowed)
    {
        if (!allowed.Contains(run.Status))
        {
            throw new ConflictException(run.Status switch
            {
                AiChunkRunStatus.Committed => "This batched run has already been saved.",
                AiChunkRunStatus.Superseded => "The requirement source or its clarifications changed after this batched run started. Start a new run.",
                AiChunkRunStatus.Failed => $"This batched run failed and cannot continue: {run.LastError}",
                AiChunkRunStatus.Abandoned => "This batched run was abandoned.",
                AiChunkRunStatus.Staged => "Every part of this batched run is done; save it.",
                _ => "This batched run still has parts to process."
            });
        }
    }

    // The source text and the context the parts were built from must be what they were at Start;
    // otherwise the run is superseded (its working state cleared) and refused.
    private async Task EnsureCurrentAsync(AiChunkRun run, IReadOnlyList<AiChunkRunPart> parts, RequirementSource source, CancellationToken cancellationToken)
    {
        var material = await Operation(run.Operation).ContextMaterialAsync(source, cancellationToken);
        if (Sha256(source.RawText) == run.SourceHash && Sha256(material) == run.ContextHash)
        {
            return;
        }

        run.Status = AiChunkRunStatus.Superseded;
        run.LastError = "The requirement source text, its clarifications or the project's business objectives changed after this run started.";
        run.CompletedAt = run.UpdatedAt = DateTime.UtcNow;
        run.LeaseId = null;
        run.LeaseExpiresAt = null;
        ClearWorkingState(parts);
        await SaveRunAsync("This batched run was just changed by another request. Reload to see its current state.", cancellationToken);
        _logger.LogInformation("Batched run {RunId} superseded: source or context changed since Start.", run.Id);
        throw new ConflictException(
            "The requirement source text, its clarifications or the project's business objectives changed after this batched run started, " +
            "so its parts no longer match. The run was closed; start a new one.");
    }

    // Prompts and responses quote the source text; once the run is over they are not kept (the raw
    // responses of a committed run are in the AI audit trail).
    private static void ClearWorkingState(IEnumerable<AiChunkRunPart> parts)
    {
        foreach (var part in parts)
        {
            part.PromptText = null;
            part.ParsedJson = null;
            part.RawResponse = null;
        }
    }

    private async Task SaveRunAsync(string conflictMessage, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            ArtifactPersistence.Undo(_db);
            throw new ConflictException(conflictMessage);
        }
    }

    private static ChunkRunFrozen Frozen(AiChunkRun run, RequirementSource source)
    {
        var plan = JsonSerializer.Deserialize<PlanData>(run.PlanJson)
            ?? throw new InvalidOperationException($"AI chunk run {run.Id} has no plan.");
        return new ChunkRunFrozen(run.Id, run.SystemPrompt, plan.KnownCodes, plan.KnownReferences, plan.ValidateSourceIds,
            plan.SourceIds, source.RawText);
    }

    private AiChunkRunResponse Respond(AiChunkRun run, IReadOnlyCollection<AiChunkRunPart> parts)
    {
        var plan = JsonSerializer.Deserialize<PlanData>(run.PlanJson);
        var perAction = Math.Max(1, PartsPerAction());
        var remaining = parts.Count(p => p.Status != AiChunkRunPartStatus.Succeeded);
        return new AiChunkRunResponse(
            run.Id, run.RequirementSourceId,
            run.Operation == AiChunkRunOperation.Analyze ? "analyze" : "generate",
            run.Status.ToString(),
            run.PartCount,
            parts.Count(p => p.Status == AiChunkRunPartStatus.Succeeded),
            parts.Count(p => p.Status == AiChunkRunPartStatus.Failed),
            parts.Count(p => p.Status == AiChunkRunPartStatus.Pending),
            perAction,
            run.Status == AiChunkRunStatus.Running ? (remaining + perAction - 1) / perAction : 0,
            plan?.BudgetProvider,
            run.LastError,
            run.Status == AiChunkRunStatus.Running && run.LastError is not null,
            run.LeaseId is not null && run.LeaseExpiresAt > DateTime.UtcNow,
            run.StartedAt, run.UpdatedAt, run.CompletedAt,
            run.ResultJson is null ? null : JsonSerializer.Deserialize<AiChunkRunResult>(run.ResultJson),
            parts.OrderBy(p => p.PartNumber)
                .Select(p => new AiChunkRunPartSummary(p.PartNumber, p.Status.ToString(), p.Attempts, p.SectionPath, p.Model, p.LastError))
                .ToList());
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
