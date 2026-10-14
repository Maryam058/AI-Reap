using System.Net;
using System.Text;
using System.Text.RegularExpressions;
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
    public static Task<(string RawResponse, T Parsed)> CallAiAndParseAsync<T>(
        IAiChatClient chatClient, ILogger logger, string operationType, string inputReference,
        string systemPrompt, string userPrompt, CancellationToken cancellationToken,
        IEnumerable<string>? knownArtifactCodes = null, IEnumerable<string>? knownSourceReferences = null) where T : class =>
        CallAndParseAsync<T>(chatClient, logger, operationType, inputReference, systemPrompt, userPrompt,
            uncondensed: false, knownArtifactCodes?.ToList(), knownSourceReferences?.ToList(), cancellationToken);

    // One part of a possibly chunked AI call: its raw response, parsed form, and the model that served it.
    public sealed record AiPart<T>(int Number, int Count, string RawResponse, T Parsed, string Model);

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

    private const string SharedContextSubsetNote =
        "[AI-REAP note: {0} of the {1} entries of this list are shown with this part - those most relevant to it; " +
        "the others are shown with the other parts.]\n";

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
    // operation with that part named - nothing is persisted from the parts before it.
    public static async Task<IReadOnlyList<AiPart<TPart>>> CallAiAndParseChunkedAsync<TWhole, TPart>(
        IAiChatClient chatClient, ILogger logger, string operationType, string inputReference,
        string systemPrompt, string sourceText, CancellationToken cancellationToken,
        string fixedContext = "", Func<TimeSpan, CancellationToken, Task>? delay = null,
        SharedContext? sharedContext = null,
        IEnumerable<string>? knownArtifactCodes = null, IEnumerable<string>? knownSourceReferences = null)
        where TWhole : class, TPart where TPart : class
    {
        var shared = sharedContext ?? SharedContext.None;
        var knownCodes = knownArtifactCodes?.ToList();
        var knownRefs = knownSourceReferences?.ToList();

        AiRequestOverBudgetException overBudget;
        try
        {
            var (raw, parsed) = await CallAndParseAsync<TWhole>(chatClient, logger, operationType, inputReference,
                systemPrompt, sourceText + shared.RenderAll() + fixedContext, uncondensed: true, knownCodes, knownRefs, cancellationToken);
            return [new AiPart<TPart>(1, 1, raw, parsed, chatClient.ModelName)];
        }
        catch (AiRequestOverBudgetException ex)
        {
            overBudget = ex;
        }

        var reserved = PromptBudgetReducer.EstimateTokens(systemPrompt) + PromptBudgetReducer.RequestOverheadTokens
            + PromptBudgetReducer.EstimateTokens(PartHeader(999, 999)) + PromptBudgetReducer.EstimateTokens(fixedContext)
            + PartRepairReserveTokens;

        // The shared list goes whole with every part while it takes at most its share of what is
        // left; beyond that, each part carries a bounded, relevance-chosen subset of it.
        var available = overBudget.InputBudget - reserved;
        var sharedTokens = shared.Items.Count == 0 ? 0
            : PromptBudgetReducer.EstimateTokens(shared.Heading) + shared.Items.Sum(PromptBudgetReducer.EstimateTokens);
        var sharedWhole = sharedTokens <= available / SharedContextBudgetDivisor;
        var sharedBudget = sharedWhole ? sharedTokens : available / SharedContextBudgetDivisor;
        var maxPartTokens = available - sharedBudget;
        if (maxPartTokens < MinPartSourceTokens)
        {
            var message =
                $"The requirement source cannot be split for {overBudget.Provider}: its input budget of {overBudget.InputBudget:N0} tokens " +
                $"leaves too little room for source text next to the ~{reserved:N0} tokens every request must carry " +
                "(instructions, context and a repair reserve). Retry when the primary AI provider is available again.";
            throw new AiProviderException(AiFailureKind.RequestRejected, overBudget.Provider, message,
                HttpStatusCode.RequestEntityTooLarge, message, inner: overBudget);
        }

        var chunks = SourceChunker.Split(sourceText, maxPartTokens);
        logger.LogInformation(
            "AI input split: {OperationType} input={InputReference} ~{Tokens} tokens exceed {Provider}'s {Budget}-token input budget; " +
            "sending {Parts} parts of at most ~{MaxPartTokens} source tokens each.",
            operationType, inputReference, overBudget.EstimatedTokens, overBudget.Provider, overBudget.InputBudget, chunks.Count, maxPartTokens);

        var sharedParts = sharedWhole
            ? chunks.Select(_ => shared.RenderAll()).ToList()
            : SplitSharedContext(shared, chunks, sharedBudget, overBudget, logger, operationType, inputReference);

        var parts = new List<AiPart<TPart>>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var number = i + 1;
            var prompt = PartHeader(number, chunks.Count) + chunks[i] + sharedParts[i] + fixedContext;

            // Safeguard: a single part (only if the provider refused a whole prompt our estimate says
            // fits) is the whole source, so it is held to TWhole's rules; the caller merges nothing
            // that could apply them later.
            var (raw, parsed) = chunks.Count == 1
                ? await CallPartAsync<TWhole>(chatClient, logger, operationType, inputReference,
                    systemPrompt, prompt, number, chunks.Count, delay, knownCodes, knownRefs, cancellationToken)
                : await CallPartAsync<TPart>(chatClient, logger, operationType, inputReference,
                    systemPrompt, prompt, number, chunks.Count, delay, knownCodes, knownRefs, cancellationToken);
            parts.Add(new AiPart<TPart>(number, chunks.Count, raw, parsed, chatClient.ModelName));
        }

        return parts;
    }

    // Each part's rendering of a shared list too large to go whole with every part. First every
    // item is placed with the part it is most relevant to (terms shared with that part's text) that
    // still has room, so each item is shown at least once; then each part's remaining room is
    // filled with other items relevant to it. Items keep their original order within a part. If
    // some item fits no part, nothing is sent: leaving clarifications out would be silent loss.
    private static List<string> SplitSharedContext(
        SharedContext shared, IReadOnlyList<string> chunks, int sharedBudget, AiRequestOverBudgetException overBudget,
        ILogger logger, string operationType, string inputReference)
    {
        var itemBudget = sharedBudget - PromptBudgetReducer.EstimateTokens(shared.Heading)
            - PromptBudgetReducer.EstimateTokens(string.Format(SharedContextSubsetNote, 99_999, 99_999));
        var itemTokens = shared.Items.Select(PromptBudgetReducer.EstimateTokens).ToArray();

        // Wording common to most items ("not yet answered - treat as still unresolved ...") says
        // nothing about which part an item belongs to.
        var itemTerms = shared.Items.Select(ContextTerms).ToList();
        HashSet<string> boilerplate = itemTerms.Count < 4 ? [] : itemTerms.SelectMany(t => t)
            .GroupBy(t => t).Where(g => g.Count() > itemTerms.Count / 2).Select(g => g.Key).ToHashSet();
        itemTerms.ForEach(t => t.ExceptWith(boilerplate));

        var chunkTerms = chunks.Select(ContextTerms).ToList();
        var relevance = chunks.Select((_, p) => itemTerms.Select(t => t.Count(chunkTerms[p].Contains)).ToArray()).ToArray();
        var room = Enumerable.Repeat(itemBudget, chunks.Count).ToArray();
        var selected = chunks.Select(_ => new SortedSet<int>()).ToArray();

        var unplaced = 0;
        for (var i = 0; i < shared.Items.Count; i++)
        {
            var item = i;
            var best = Enumerable.Range(0, chunks.Count)
                .Where(p => itemTokens[item] <= room[p])
                .OrderByDescending(p => relevance[p][item]).ThenByDescending(p => room[p])
                .DefaultIfEmpty(-1).First();
            if (best < 0)
            {
                unplaced++;
                continue;
            }

            selected[best].Add(item);
            room[best] -= itemTokens[item];
        }

        if (unplaced > 0)
        {
            var message =
                $"The requirement source was split into {chunks.Count} parts for {overBudget.Provider}, but {unplaced:N0} of its " +
                $"{shared.Items.Count:N0} clarifications do not fit in any part's {overBudget.InputBudget:N0}-token input budget, so " +
                "they could not be shown to the AI. Nothing was sent. Retry when the primary AI provider is available again.";
            logger.LogWarning(
                "AI input not sent: {OperationType} input={InputReference} {Unplaced} of {Items} shared context items fit no part.",
                operationType, inputReference, unplaced, shared.Items.Count);
            throw new AiProviderException(AiFailureKind.RequestRejected, overBudget.Provider, message,
                HttpStatusCode.RequestEntityTooLarge, message, inner: overBudget);
        }

        for (var p = 0; p < chunks.Count; p++)
        {
            var part = p;
            foreach (var item in Enumerable.Range(0, shared.Items.Count)
                .Where(i => relevance[part][i] > 0 && !selected[part].Contains(i))
                .OrderByDescending(i => relevance[part][i]).ThenBy(i => i))
            {
                if (itemTokens[item] <= room[part])
                {
                    selected[part].Add(item);
                    room[part] -= itemTokens[item];
                }
            }
        }

        logger.LogInformation(
            "AI shared context split: {OperationType} input={InputReference} {Items} items (~{Tokens} tokens) over {Parts} parts, " +
            "at most ~{Budget} tokens each; items per part: {PerPart}.",
            operationType, inputReference, shared.Items.Count, itemTokens.Sum(), chunks.Count, itemBudget,
            string.Join(",", selected.Select(s => s.Count)));

        return selected.Select(s => s.Count == 0 ? string.Empty
            : shared.Heading + string.Format(SharedContextSubsetNote, s.Count, shared.Items.Count) + string.Concat(s.Select(i => shared.Items[i])))
            .ToList();
    }

    private static readonly Regex ContextWord =
        new(@"\b[A-Za-z][A-Za-z-]{3,}\b", RegexOptions.Compiled);

    private static HashSet<string> ContextTerms(string text) =>
        ContextWord.Matches(text).Select(m => m.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    private static async Task<(string RawResponse, T Parsed)> CallPartAsync<T>(
        IAiChatClient chatClient, ILogger logger, string operationType, string inputReference,
        string systemPrompt, string prompt, int number, int count,
        Func<TimeSpan, CancellationToken, Task>? delay, List<string>? knownCodes, List<string>? knownRefs,
        CancellationToken cancellationToken) where T : class
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
                        systemPrompt, prompt, uncondensed: true, knownCodes, knownRefs, cancellationToken);
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
        List<string>? knownCodes, List<string>? knownRefs, CancellationToken cancellationToken) where T : class
    {
        var prompt = userPrompt;

        for (var attempt = 1; ; attempt++)
        {
            var rawResponse = await CallAiAsync(chatClient, logger, operationType, inputReference, systemPrompt, prompt, uncondensed, cancellationToken);

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
