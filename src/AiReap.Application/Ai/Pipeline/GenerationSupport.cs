using System.Net;
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

    // The upstream artifacts a generation prompt builds on: LoadArtifactsAsync without the rejected
    // ones (GenerationInputs). Counts and metrics keep using LoadArtifactsAsync.
    public static Task<List<Artifact>> LoadGenerationInputsAsync(
        IAiReapDbContext db, Guid requirementSourceId, ArtifactType type, CancellationToken cancellationToken) =>
        GenerationInputs.For(db.Artifacts, requirementSourceId, type).ToListAsync(cancellationToken);

    // The heading over the eligible FR list ("- FR-001: Title" lines) in every stage that builds on
    // FRs. The development stub (StubAiChatClient) cites from this list only, never from FR-like IDs
    // in the raw source text, so the two must use this one constant.
    public const string FunctionalRequirementsHeading = "Functional requirements derived so far:";

    // A4b Phase 4 - the eligible-FR block of the user story, data entity, API and task prompts:
    // BuildContextBlock under FunctionalRequirementsHeading, with " (source IDs: FR-012)" after the title
    // of a requirement that records the IDs written in the source it came from. Such an ID can equal a
    // different stored code, so the prompt shows the mapping; the stored code on the left stays the only
    // citation. A requirement without source IDs keeps the "- FR-001: Title" line exactly. Business rules
    // and solution design keep the plain block.
    public static string BuildFunctionalRequirementBlock(IEnumerable<Artifact> functionalRequirements) =>
        BuildContextBlock(FunctionalRequirementsHeading, functionalRequirements.Select(fr => (fr.Code, fr.Title + SourceIdsSuffix(fr))));

    private static string SourceIdsSuffix(Artifact functionalRequirement)
    {
        List<string> sourceIds;
        try
        {
            sourceIds = string.IsNullOrWhiteSpace(functionalRequirement.DataJson)
                ? []
                : FunctionalRequirementContext.Read(functionalRequirement).SourceIds ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            sourceIds = []; // an unreadable payload shows the plain line, as before
        }

        var ids = sourceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return ids.Count == 0 ? string.Empty : $" (source IDs: {string.Join(", ", ids)})";
    }

    public static string BuildContextBlock(string heading, IEnumerable<Artifact> artifacts) =>
        BuildContextBlock(heading, artifacts.Select(a => (a.Code, a.Title)));

    public static string BuildContextBlock(string heading, IEnumerable<(string Code, string Title)> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder($"\n\n{heading}\n");
        foreach (var (code, title) in list)
        {
            sb.Append($"- {code}: {title}\n");
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
    public static Task<(string RawResponse, T Parsed)> CallAiAndParseAsync<T>(
        IAiChatClient chatClient, ILogger logger, string operationType, string inputReference,
        string systemPrompt, string userPrompt, CancellationToken cancellationToken,
        IEnumerable<string>? knownArtifactCodes = null, IEnumerable<string>? knownSourceReferences = null) where T : class =>
        CallAndParseAsync<T>(chatClient, logger, operationType, inputReference, systemPrompt, userPrompt,
            uncondensed: false, knownArtifactCodes?.ToList(), knownSourceReferences?.ToList(), null, cancellationToken);

    // P0-5 — the same call for the later pipeline stages, which send their prompt whole or not at all.
    // CallAiAndParseAsync lets a per-request budget (Groq) condense an over-budget prompt by dropping
    // passages, which can silently lose requirements; here the prompt and its one repair attempt are
    // sent uncondensed, and a request the serving provider's budget cannot take is refused before it
    // is sent (AiStageOverBudgetException). Provider routing, fallback, cool-down, retries, logging and
    // validation are those of CallAiAndParseAsync.
    public static async Task<(string RawResponse, T Parsed)> CallAiAndParseLosslessAsync<T>(
        IAiChatClient chatClient, ILogger logger, string operationType, string inputReference,
        string systemPrompt, string userPrompt, CancellationToken cancellationToken,
        IEnumerable<string>? knownArtifactCodes = null, IEnumerable<string>? knownSourceReferences = null) where T : class
    {
        try
        {
            return await CallAndParseAsync<T>(chatClient, logger, operationType, inputReference, systemPrompt, userPrompt,
                uncondensed: true, knownArtifactCodes?.ToList(), knownSourceReferences?.ToList(), null, cancellationToken);
        }
        catch (AiRequestOverBudgetException ex)
        {
            throw RefuseOverBudget(logger, operationType, inputReference, ex);
        }
    }

    // The refusal CallAiAndParseLosslessAsync raises for a request the serving provider's budget cannot
    // take, logged the same way; also used by callers that make their own uncondensed call (ReviewAgent).
    public static AiStageOverBudgetException RefuseOverBudget(
        ILogger logger, string operationType, string inputReference, AiRequestOverBudgetException ex)
    {
        var refused = new AiStageOverBudgetException(operationType, ex.Data[RepairAttemptKey] is true, ex);
        logger.LogWarning(
            "AI stage request refused, not sent: {OperationType} input={InputReference} repairAttempt={RepairAttempt} provider={Provider} " +
            "estimatedInputTokens={EstimatedTokens} inputBudget={InputBudget} overBudgetTokens={OverBudgetTokens}. " +
            "The stage sends its input whole or not at all; nothing was shortened or saved.",
            operationType, inputReference, refused.RepairAttempt, ex.Provider,
            refused.EstimatedInputTokens, refused.InputBudget, refused.OverBudgetTokens);
        return refused;
    }

    // The uncondensed call and validation of CallAiAndParseLosslessAsync, but a request over the serving
    // provider's budget surfaces as the AiRequestOverBudgetException itself (nothing was sent; when it
    // was the repair attempt, Data[RepairAttemptKey] is true), so the caller can decide what to do -
    // CopilotService fits its optional context to that budget.
    public static Task<(string RawResponse, T Parsed)> CallAiAndParseUncondensedAsync<T>(
        IAiChatClient chatClient, ILogger logger, string operationType, string inputReference,
        string systemPrompt, string userPrompt, CancellationToken cancellationToken,
        IEnumerable<string>? knownArtifactCodes = null) where T : class =>
        CallAndParseAsync<T>(chatClient, logger, operationType, inputReference, systemPrompt, userPrompt,
            uncondensed: true, knownArtifactCodes?.ToList(), null, null, cancellationToken);

    // Exception.Data key set on an AiRequestOverBudgetException raised by the repair attempt rather than
    // the first request, so a refusal does not imply that nothing at all was sent.
    public const string RepairAttemptKey = "AiRepairAttempt";

    // One part of a possibly chunked AI call: its raw response, parsed form, and the model that served it.
    // Section: the heading the part starts under (SourceChunker.SectionAnchors); null for a whole source.
    // SharedContextWhole: the part carried the whole SharedContext; false when a split source gave each
    // part only the items most relevant to it (P0-7 RequirementCompleteness reports it).
    public sealed record AiPart<T>(int Number, int Count, string RawResponse, T Parsed, string Model, string? Section = null,
        bool SharedContextWhole = true);

    // Estimated room kept free in every part for the repair instruction, so a part's one repair
    // attempt fits the same budget as the part itself.
    public const int PartRepairReserveTokens = 600;

    // Smallest slice of source worth sending on its own; below this the fixed parts leave no room.
    public const int MinPartSourceTokens = 500;

    // A part that hits the provider's per-minute limit waits this long at most, once, and is re-sent.
    public static readonly TimeSpan MaxPartRateLimitWait = TimeSpan.FromSeconds(65);

    // Exception.Data keys the API error mapping reads to name the failed part in its response.
    public const string FailedPartKey = "AiFailedPart";
    public const string PartCountKey = "AiPartCount";

    private const string PartHeaderTemplate =
        "[AI-REAP note: this requirement source is too large for a single request to the AI provider serving it, so it " +
        "was split at section and paragraph boundaries into {1} parts that are processed separately and merged " +
        "afterwards. This is part {0} of {1}. Base the answer only on the text of this part. Other parts cover the " +
        "rest of the source, so do not report something as missing just because this part does not mention it; " +
        "report only what this part itself leaves unclear.]\n\n";

    public static string PartHeader(int number, int count) => string.Format(PartHeaderTemplate, number, count);

    // A context list that grows with the source - the clarification Q&A a chunked analysis of a
    // large source produces - and so may not fit next to every part of it. A whole-source request
    // carries all of it, rendered exactly as the list it replaces (Heading + items); a split
    // source gives each part the items most relevant to it within a bounded share of the budget,
    // and every item goes with at least one part (CallAiAndParseChunkedAsync).
    public sealed record SharedContext(string Heading, IReadOnlyList<string> Items)
    {
        public static readonly SharedContext None = new(string.Empty, []);

        public string RenderAll() => Items.Count == 0 ? string.Empty : Heading + string.Concat(Items);
    }

    // Share of a part's input budget (after its fixed parts) a SharedContext may take when the
    // whole list does not fit in it; the rest is source text.
    public const int SharedContextBudgetDivisor = 3;

    // CallAiAndParseAsync for a stage whose whole input is one requirement source, without ever
    // condensing it. The source (+ sharedContext + fixedContext) is first sent whole and
    // uncondensed. If the provider serving the call has a per-request budget the prompt does not
    // fit (Groq after a Gemini fallback; Gemini has none, so its path is unchanged), nothing is
    // sent and the source is split by SourceChunker into parts sized to that budget minus
    // everything each request must also carry: the system prompt, request framing, the part
    // header, fixedContext, the part's share of sharedContext and a repair reserve. Each part is
    // its own call with its own single repair attempt; the caller merges the parsed parts
    // (TWhole's validation applies to a whole-source response, TPart's to one part). No part's
    // content is dropped and no synthesis request is made. A part that fails ends the whole
    // operation with that part named - nothing is persisted from the parts before it. A source
    // needing more parts than the provider allows (its Ai:<Provider>:MaxChunkedParts) is refused
    // before any part is sent.
    public static async Task<IReadOnlyList<AiPart<TPart>>> CallAiAndParseChunkedAsync<TWhole, TPart>(
        IAiChatClient chatClient, ILogger logger, string operationType, string inputReference,
        string systemPrompt, string sourceText, CancellationToken cancellationToken,
        string fixedContext = "", Func<TimeSpan, CancellationToken, Task>? delay = null,
        SharedContext? sharedContext = null,
        IEnumerable<string>? knownArtifactCodes = null, IEnumerable<string>? knownSourceReferences = null,
        bool validateSourceIds = false)
        where TWhole : class, TPart where TPart : class
    {
        var shared = sharedContext ?? SharedContext.None;
        var knownCodes = knownArtifactCodes?.ToList();
        var knownRefs = knownSourceReferences?.ToList();

        AiRequestOverBudgetException overBudget;
        try
        {
            var (raw, parsed) = await CallAndParseAsync<TWhole>(chatClient, logger, operationType, inputReference,
                systemPrompt, ChunkPlanner.WholePrompt(sourceText, shared, fixedContext), uncondensed: true, knownCodes, knownRefs,
                validateSourceIds ? SourceIdScope.For(sourceText, sourceText) : null, cancellationToken);
            return [new AiPart<TPart>(1, 1, raw, parsed, chatClient.ModelName)];
        }
        catch (AiRequestOverBudgetException ex)
        {
            overBudget = ex;
        }

        // The same plan the dry-run AI plan (AiPlanService) reports for this provider.
        var plan = ChunkPlanner.Split(systemPrompt, sourceText, fixedContext, shared,
            overBudget.InputBudget, overBudget.MaxParts, overBudget.EstimatedTokens);
        if (plan.Outcome == ChunkPlanOutcome.CannotSplit)
        {
            var message = PrimaryNote(overBudget) +
                $"The requirement source cannot be split for {overBudget.Provider}: its input budget of {overBudget.InputBudget:N0} tokens " +
                $"leaves too little room for source text next to the ~{plan.ReservedTokensPerPart:N0} tokens every request must carry " +
                "(instructions, context and a repair reserve). Nothing was sent and nothing was saved.";
            throw new AiSourceCannotBeSplitException(overBudget.Provider, message, overBudget.InputBudget, plan.ReservedTokensPerPart, inner: overBudget);
        }

        var chunks = plan.Chunks;
        if (plan.Outcome == ChunkPlanOutcome.TooManyParts)
        {
            throw TooManyParts(overBudget, chunks.Count, logger, operationType, inputReference);
        }

        logger.LogInformation(
            "AI input split: {OperationType} input={InputReference} ~{Tokens} tokens exceed {Provider}'s {Budget}-token input budget; " +
            "sending {Parts} parts of at most ~{MaxPartTokens} source tokens each.",
            operationType, inputReference, overBudget.EstimatedTokens, overBudget.Provider, overBudget.InputBudget, chunks.Count, plan.MaxPartSourceTokens);

        if (plan.Outcome == ChunkPlanOutcome.SharedContextDoesNotFit)
        {
            throw SharedContextDoesNotFit(overBudget, plan, logger, operationType, inputReference);
        }

        if (!plan.SharedContextWhole)
        {
            logger.LogInformation(
                "AI shared context split: {OperationType} input={InputReference} {Items} items (~{Tokens} tokens) over {Parts} parts, " +
                "at most ~{Budget} tokens each; items per part: {PerPart}.",
                operationType, inputReference, shared.Items.Count, plan.SharedContextTokens - PromptBudgetReducer.EstimateTokens(shared.Heading),
                chunks.Count, plan.SharedContextItemBudget, string.Join(",", plan.SharedItemsPerPart));
        }

        var sharedParts = plan.SharedContextParts;
        var anchors = SourceChunker.SectionAnchors(chunks);

        var parts = new List<AiPart<TPart>>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var number = i + 1;
            var prompt = PartPrompt(number, chunks.Count, chunks[i], sharedParts[i], fixedContext);
            var (raw, parsed) = await CallPlannedPartAsync<TWhole, TPart>(chatClient, logger, operationType, inputReference,
                systemPrompt, prompt, number, chunks.Count, delay, knownCodes, knownRefs,
                validateSourceIds ? SourceIdScope.For(chunks[i], sourceText) : null, cancellationToken);
            parts.Add(new AiPart<TPart>(number, chunks.Count, raw, parsed, chatClient.ModelName, anchors[i], plan.SharedContextWhole));
        }

        return parts;
    }

    // The user prompt of one planned part, exactly as CallAiAndParseChunkedAsync sends it (a batched
    // run, AiChunkRunService, freezes it at Start).
    public static string PartPrompt(int number, int count, string chunk, string sharedPart, string fixedContext) =>
        PartHeader(number, count) + chunk + sharedPart + fixedContext;

    // One planned part: its own single repair attempt, one wait on a short per-minute rate limit,
    // and failures labelled with the part. A single part (only if the provider refused a whole
    // prompt our estimate says fits) is the whole source, so it is held to TWhole's rules; the
    // caller merges nothing that could apply them later.
    public static async Task<(string RawResponse, TPart Parsed)> CallPlannedPartAsync<TWhole, TPart>(
        IAiChatClient chatClient, ILogger logger, string operationType, string inputReference,
        string systemPrompt, string prompt, int number, int count, Func<TimeSpan, CancellationToken, Task>? delay,
        IReadOnlyList<string>? knownCodes, IReadOnlyList<string>? knownRefs, SourceIdScope? sourceIds,
        CancellationToken cancellationToken)
        where TWhole : class, TPart where TPart : class
    {
        var codes = knownCodes?.ToList();
        var refs = knownRefs?.ToList();
        return count == 1
            ? await CallPartAsync<TWhole>(chatClient, logger, operationType, inputReference,
                systemPrompt, prompt, number, count, delay, codes, refs, sourceIds, cancellationToken)
            : await CallPartAsync<TPart>(chatClient, logger, operationType, inputReference,
                systemPrompt, prompt, number, count, delay, codes, refs, sourceIds, cancellationToken);
    }

    // A source needing more parts than the provider allows is refused before any part is sent:
    // every part may use the provider's whole per-request budget, so a long run of parts can spend
    // a daily quota and still fail part-way, saving nothing. The figures are upper-bound estimates
    // from our own token counting - the provider's remaining quota is not known here.
    private static AiChunkLimitExceededException TooManyParts(
        AiRequestOverBudgetException overBudget, int parts, ILogger logger, string operationType, string inputReference)
    {
        var setting = overBudget.MaxPartsSetting ?? "the configured maximum";
        var estimate = overBudget.MaxRequestTokens > 0
            ? $" Estimated upper bound: about {parts * (long)overBudget.MaxRequestTokens:N0} tokens " +
              $"({parts} requests of up to {overBudget.MaxRequestTokens:N0} tokens each) and, if that request size is " +
              $"{overBudget.Provider}'s per-minute token limit, at least about {parts} minutes."
            : string.Empty;
        var message = PrimaryNote(overBudget) +
            $"The requirement source would have to be sent to {overBudget.Provider} in {parts} parts, more than the " +
            $"{overBudget.MaxParts} allowed ({setting}={overBudget.MaxParts}).{estimate} These are estimates, not a check of " +
            $"the remaining {overBudget.Provider} quota. Nothing was sent and nothing was saved. Retry when the primary AI " +
            "provider is available again, or split the source into smaller requirement sources.";
        logger.LogWarning(
            "AI input not sent: {OperationType} input={InputReference} needs {Parts} parts for {Provider}, more than the {MaxParts} allowed.",
            operationType, inputReference, parts, overBudget.Provider, overBudget.MaxParts);
        return new AiChunkLimitExceededException(overBudget.Provider, message, parts, overBudget.MaxParts, setting, inner: overBudget);
    }

    // Leads a refusal with the actual blocker when the budgeted provider was only serving as the
    // fallback: e.g. "Primary AI provider not used - Gemini failed: Gemini quota exhausted (...)."
    // The outcome is the primary's sanitized AiProviderException message (FallbackAiChatClient).
    private static string PrimaryNote(AiRequestOverBudgetException overBudget) =>
        overBudget.Primary is { } primary ? $"Primary AI provider not used - {primary.Outcome.TrimEnd('.', ' ')}. " : string.Empty;

    // Some clarifications fit no part (ChunkPlanner): nothing is sent, since leaving them out would
    // be silent loss.
    private static AiProviderException SharedContextDoesNotFit(
        AiRequestOverBudgetException overBudget, ChunkPlan plan, ILogger logger, string operationType, string inputReference)
    {
        var message = PrimaryNote(overBudget) +
            $"The requirement source was split into {plan.Chunks.Count} parts for {overBudget.Provider}, but {plan.UnplacedSharedItems:N0} of its " +
            $"{plan.SharedContextItems:N0} clarifications do not fit in any part's {overBudget.InputBudget:N0}-token input budget, so " +
            "they could not be shown to the AI. Nothing was sent. Retry when the primary AI provider is available again.";
        logger.LogWarning(
            "AI input not sent: {OperationType} input={InputReference} {Unplaced} of {Items} shared context items fit no part.",
            operationType, inputReference, plan.UnplacedSharedItems, plan.SharedContextItems);
        return new AiProviderException(AiFailureKind.RequestRejected, overBudget.Provider, message,
            HttpStatusCode.RequestEntityTooLarge, message, inner: overBudget);
    }

    private static async Task<(string RawResponse, T Parsed)> CallPartAsync<T>(
        IAiChatClient chatClient, ILogger logger, string operationType, string inputReference,
        string systemPrompt, string prompt, int number, int count,
        Func<TimeSpan, CancellationToken, Task>? delay, List<string>? knownCodes, List<string>? knownRefs,
        SourceIdScope? sourceIds, CancellationToken cancellationToken) where T : class
    {
        var partReference = $"{inputReference}#part-{number}-of-{count}";
        var label = $"Part {number} of {count} of the requirement source";
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await CallAndParseAsync<T>(chatClient, logger, operationType, partReference,
                        systemPrompt, prompt, uncondensed: true, knownCodes, knownRefs, sourceIds, cancellationToken);
                }
                catch (AiProviderException ex) when (attempt == 1
                    && ex is { Kind: AiFailureKind.RateLimited, QuotaExhausted: false, RetryAfter: { } wait }
                    && wait <= MaxPartRateLimitWait)
                {
                    // Parts are sent back to back, so a per-minute token limit is expected to bite
                    // between them: wait out the provider's hint once, then send this part again.
                    logger.LogWarning(
                        "AI rate limit on {OperationType} input={InputReference}; waiting {WaitSeconds:0}s before re-sending it.",
                        operationType, partReference, ex.RetryAfter!.Value.TotalSeconds);
                    await (delay ?? Task.Delay)(ex.RetryAfter!.Value, cancellationToken);
                }
            }
        }
        catch (AiRequestOverBudgetException ex)
        {
            var message = $"{label} still exceeds the {ex.InputBudget:N0}-token input budget of {ex.Provider} " +
                $"(about {ex.EstimatedTokens:N0} tokens). Nothing was saved.";
            throw PartFailure(new AiProviderException(AiFailureKind.RequestRejected, ex.Provider, message,
                HttpStatusCode.RequestEntityTooLarge, message, inner: ex), number, count);
        }
        catch (AiProviderException ex)
        {
            throw PartFailure(new AiProviderException(ex.Kind, ex.Provider, $"{label} failed: {ex.Message} Nothing was saved.",
                ex.ProviderStatusCode, ex.ProviderMessage is null ? null : $"{label} failed: {ex.ProviderMessage} Nothing was saved.",
                ex.RetryAfter, ex, ex.QuotaExhausted), number, count);
        }
        catch (AiOutputValidationException ex)
        {
            throw PartFailure(new AiOutputValidationException($"{label}: {ex.Message}",
                ex.Errors.Select(e => $"{label}: {e}").ToList(), ex.Repairable), number, count);
        }
    }

    private static Exception PartFailure(Exception ex, int number, int count)
    {
        ex.Data[FailedPartKey] = number;
        ex.Data[PartCountKey] = count;
        return ex;
    }

    private static async Task<(string RawResponse, T Parsed)> CallAndParseAsync<T>(
        IAiChatClient chatClient, ILogger logger, string operationType, string inputReference,
        string systemPrompt, string userPrompt, bool uncondensed,
        List<string>? knownCodes, List<string>? knownRefs, SourceIdScope? sourceIds, CancellationToken cancellationToken) where T : class
    {
        var prompt = userPrompt;

        for (var attempt = 1; ; attempt++)
        {
            string rawResponse;
            try
            {
                rawResponse = await CallAiAsync(chatClient, logger, operationType, inputReference, systemPrompt, prompt, uncondensed, cancellationToken);
            }
            catch (AiRequestOverBudgetException ex) when (attempt > 1)
            {
                // The repair prompt (request plus validation errors) is larger than the first request.
                ex.Data[RepairAttemptKey] = true;
                throw;
            }

            try
            {
                return (rawResponse, AiJsonParser.Parse<T>(rawResponse, knownCodes, knownRefs, sourceIds));
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
        string systemPrompt, string userPrompt, bool uncondensed, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "AI call starting: {OperationType} model={Model} input={InputReference}",
            operationType, chatClient.ModelName, inputReference);

        string rawResponse;
        try
        {
            rawResponse = uncondensed
                ? await chatClient.CompleteUncondensedAsync(systemPrompt, userPrompt, cancellationToken)
                : await chatClient.CompleteAsync(systemPrompt, userPrompt, cancellationToken);
        }
        catch (AiProviderException ex)
        {
            // Expected, classified failure - message only (it is already sanitized), no stack trace noise.
            logger.LogError(
                "AI call failed: {OperationType} model={Model} input={InputReference} kind={Kind} status={Status} message={Message}",
                operationType, chatClient.ModelName, inputReference, ex.Kind, (int?)ex.ProviderStatusCode, ex.Message);
            throw;
        }
        catch (AiRequestOverBudgetException ex)
        {
            // Nothing was sent; the caller splits the input.
            logger.LogInformation(
                "AI call not sent whole: {OperationType} input={InputReference} {Message}", operationType, inputReference, ex.Message);
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

    // The candidates a response cites, matched the way AiResponseValidator.CodesExist accepts a code
    // (trimmed, case-insensitive) and resolved to the stored artifact, each at most once, in candidate
    // order. So a citation that passed validation always becomes its link, and a repeated code never
    // adds a second relationship (unique on source, type, target). Codes outside the candidates are
    // dropped here; validation has already rejected them when the candidates were the known codes.
    public static List<Artifact> ResolveCited(IEnumerable<string>? codes, IEnumerable<Artifact> candidates)
    {
        var cited = (codes ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.Where(a => cited.Contains(a.Code.Trim())).DistinctBy(a => a.Id).ToList();
    }

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
