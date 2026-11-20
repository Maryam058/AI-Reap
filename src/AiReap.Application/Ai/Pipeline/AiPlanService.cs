using AiReap.Application.Common;
using AiReap.Application.Knowledge;
using AiReap.Application.Persistence;
using AiReap.Application.Traceability;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Ai.Pipeline;

public interface IAiPlanService
{
    Task<AiPlanResult> PlanAsync(Guid requirementSourceId, AiPlanOperation operation, CancellationToken cancellationToken = default);
}

// The dry-run AI plan: before Analyze or Generate runs, how each configured provider would take the
// requirement source - whole, in N parts, or refused - and why. It builds the prompt pieces the real
// service builds (same system prompt, source, clarifications, objectives and excerpt formatting)
// and plans them with ChunkPlanner, the code GenerationSupport.CallAiAndParseChunkedAsync runs, so
// a refusal shown here is the refusal a run would hit. It never calls an AI or embedding provider
// (the provider limits are read off the client by IAiRouteInspector) and never writes: business
// objectives are previewed, not recorded.
public class AiPlanService : IAiPlanService
{
    private readonly IAiReapDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly IKnowledgeRetriever _retriever;
    private readonly IAiRouteInspector _routeInspector;
    private readonly IAiBatchSettings _batchSettings;
    private readonly IAiChunkRunService _chunkRuns;

    public AiPlanService(
        IAiReapDbContext db, IProjectAccessService projectAccess, IKnowledgeRetriever retriever, IAiRouteInspector routeInspector,
        IAiBatchSettings batchSettings, IAiChunkRunService chunkRuns)
    {
        _db = db;
        _projectAccess = projectAccess;
        _retriever = retriever;
        _routeInspector = routeInspector;
        _batchSettings = batchSettings;
        _chunkRuns = chunkRuns;
    }

    private sealed record PlanInputs(
        string SystemPrompt, string SourceText, string FixedContextMin, string FixedContextMax,
        GenerationSupport.SharedContext Shared, int BusinessObjectives, int ExcerptCandidates);

    public async Task<AiPlanResult> PlanAsync(Guid requirementSourceId, AiPlanOperation operation, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == requirementSourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"RequirementSource {requirementSourceId} not found.");
        await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);

        var inputs = operation == AiPlanOperation.Analyze
            ? new PlanInputs(RequirementAnalysisService.SystemPrompt, source.RawText, string.Empty, string.Empty,
                GenerationSupport.SharedContext.None, 0, 0)
            : await GenerationInputsAsync(source.ProjectId, source.Id, source.RawText, cancellationToken);

        var route = _routeInspector.Inspect();
        var sourceTokens = PromptBudgetReducer.EstimateTokens(source.RawText);
        var context = new AiPlanContext(
            sourceTokens,
            PromptBudgetReducer.EstimateTokens(inputs.SystemPrompt),
            new AiPlanRange(PromptBudgetReducer.EstimateTokens(inputs.FixedContextMin), PromptBudgetReducer.EstimateTokens(inputs.FixedContextMax)),
            inputs.Shared.Items.Count,
            inputs.Shared.Items.Count == 0 ? 0 : PromptBudgetReducer.EstimateTokens(inputs.Shared.RenderAll()),
            inputs.BusinessObjectives,
            inputs.ExcerptCandidates);

        var providers = new List<AiPlanProvider>();
        if (route.Primary is { } primary)
        {
            providers.Add(PlanProvider("Primary", primary, route.Fallback is null ? null : route.PrimaryCooldownUntil, inputs, operation, sourceTokens));
        }

        if (route.Fallback is { } fallback)
        {
            providers.Add(PlanProvider("Fallback", fallback, null, inputs, operation, sourceTokens));
        }

        var expectedFirst = route.Primary is null ? null
            : route.Fallback is not null && route.PrimaryCooldownUntil is not null ? route.Fallback.Provider
            : route.Primary.Provider;

        return new AiPlanResult(
            operation == AiPlanOperation.Analyze ? "analyze" : "generate",
            source.Id,
            DateTime.UtcNow,
            context,
            new AiPlanRoute(route.Primary?.Provider, route.Fallback?.Provider, route.Fallback is null ? null : route.PrimaryCooldownUntil,
                expectedFirst, route.ConfigurationError),
            providers,
            Overall(route, providers),
            Caveats(operation, context, route),
            await BatchAsync(route, inputs, operation, source.Id, cancellationToken));
    }

    // Read only when batch mode is on.
    private async Task<AiPlanBatch> BatchAsync(
        AiRouteSnapshot route, PlanInputs inputs, AiPlanOperation operation, Guid sourceId, CancellationToken cancellationToken)
    {
        if (!_batchSettings.BatchModeEnabled())
        {
            return new AiPlanBatch(false, 0, null, null, null, null, false,
                "Batch mode is disabled (Ai:Groq:MaxBatchedRunParts=0).", null);
        }

        var max = _batchSettings.MaxBatchedRunParts;
        var active = await _chunkRuns.GetActiveAsync(sourceId,
            operation == AiPlanOperation.Analyze ? Domain.Enums.AiChunkRunOperation.Analyze : Domain.Enums.AiChunkRunOperation.Generate,
            cancellationToken);
        if (AiChunkRunService.BudgetProvider(route) is not { InputBudget: { } budget } provider)
        {
            return new AiPlanBatch(true, max, null, null, null, null, false,
                "No configured AI provider has a per-request size limit, so batches are never needed.", active);
        }

        var perAction = provider.MaxParts > 0 ? provider.MaxParts : max;
        var plan = ChunkPlanner.Plan(inputs.SystemPrompt, inputs.SourceText, inputs.FixedContextMax, inputs.Shared, budget, maxParts: 0);
        int? parts = plan.Outcome switch
        {
            ChunkPlanOutcome.Whole => 1,
            ChunkPlanOutcome.Split => plan.Chunks.Count,
            _ => null
        };
        var reason = active is not null ? "A batched run is already active for this source."
            : plan.Outcome == ChunkPlanOutcome.Whole ? $"The source fits in one {provider.Provider} request; run it normally."
            : plan.Outcome == ChunkPlanOutcome.CannotSplit ? $"{provider.Provider}'s input budget leaves too little room for source text in a part."
            : plan.Outcome == ChunkPlanOutcome.SharedContextDoesNotFit ? $"Some clarifications would not fit in any part for {provider.Provider}."
            : parts > max ? $"A batched run would need {parts} parts; at most {max} are allowed (Ai:Groq:MaxBatchedRunParts={max})."
            : null;
        return new AiPlanBatch(true, max, provider.Provider, perAction, parts,
            parts is { } p && plan.Outcome == ChunkPlanOutcome.Split ? (p + perAction - 1) / perAction : null,
            reason is null, reason, active);
    }

    // The pieces RequirementGenerationService.GenerateAsync builds, without its two side effects:
    // objectives are previewed instead of recorded, and document excerpts are bounded instead of
    // retrieved (retrieval embeds the source with an embedding provider).
    private async Task<PlanInputs> GenerationInputsAsync(Guid projectId, Guid sourceId, string rawText, CancellationToken cancellationToken)
    {
        var clarifications = await ClarificationContextBuilder.BuildItemsAsync(_db, sourceId, withCodes: true, cancellationToken);
        var project = await _db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, cancellationToken);
        var objectives = await BusinessObjectives.PreviewAsync(_db, projectId, project.Objectives, cancellationToken);
        var objectiveContext = GenerationSupport.BuildContextBlock(RequirementGenerationService.ObjectivesHeading, objectives);

        // RetrieveAsync returns nothing for a blank query; otherwise at most DocumentExcerpts chunks
        // that pass a similarity threshold - so between none and the largest candidates.
        var candidates = string.IsNullOrWhiteSpace(rawText)
            ? []
            : await _retriever.LargestCandidatesAsync(projectId, RequirementGenerationService.DocumentExcerpts, cancellationToken);

        return new PlanInputs(
            RequirementGenerationService.SystemPrompt, rawText,
            objectiveContext, objectiveContext + RequirementGenerationService.DocumentContext(candidates),
            new GenerationSupport.SharedContext(ClarificationContextBuilder.Heading, clarifications),
            objectives.Count, candidates.Count);
    }

    private static AiPlanProvider PlanProvider(
        string role, AiProviderLimits limits, DateTimeOffset? cooldownUntil, PlanInputs inputs, AiPlanOperation operation, int sourceTokens)
    {
        var reasons = new List<AiPlanReason>();
        var availability = limits.IsStub ? AiPlanAvailability.Stub
            : cooldownUntil is not null ? AiPlanAvailability.CoolingDown
            : AiPlanAvailability.QuotaUnknown;

        if (cooldownUntil is { } until)
        {
            reasons.Add(new AiPlanReason("CoolingDown", "Blocking",
                $"{limits.Provider} answered with a rate limit or exhausted quota earlier and is skipped until {until:u} " +
                "(recorded by this server only; a restart forgets it).", Until: until));
        }
        else if (limits.IsStub)
        {
            reasons.Add(new AiPlanReason("StubProvider", "Warning",
                "No AI provider key is configured: the development stub returns canned data, not real AI output."));
        }
        else
        {
            reasons.Add(new AiPlanReason("QuotaUnknown", "Info",
                $"{limits.Provider}'s remaining quota is not known until a request is sent; this plan does not contact it."));
        }

        if (!limits.LimitsKnown)
        {
            reasons.Add(new AiPlanReason("LimitsUnknown", "Warning",
                $"The request limits of {limits.Provider} cannot be read, so how it would take this source is uncertain."));
            return Provider(role, limits, cooldownUntil is not null ? AiPlanStatus.Blocked : AiPlanStatus.Uncertain, availability,
                cooldownUntil, reasons, whole: false, parts: null, estimatedTokens: PromptBudgetReducer.EstimateRequestTokens(inputs.SystemPrompt,
                    ChunkPlanner.WholePrompt(inputs.SourceText, inputs.Shared, inputs.FixedContextMax)), plan: null)
                with { SizeStatus = AiPlanStatus.Uncertain };
        }

        // Generate on a provider with an output ceiling is staged (GenerationSupport, AiOutputStaging):
        // planned with ChunkPlanner.PlanStaged, exactly as the real run plans it.
        var staging = operation == AiPlanOperation.Generate ? limits.OutputStaging : null;
        ChunkPlan PlanFor(string fixedContext) => staging is not null
            ? ChunkPlanner.PlanStaged(inputs.SystemPrompt, inputs.SourceText, fixedContext, inputs.Shared, staging)
            : ChunkPlanner.Plan(inputs.SystemPrompt, inputs.SourceText, fixedContext, inputs.Shared, limits.InputBudget, limits.MaxParts);
        var partsSetting = staging?.MaxPartsSetting ?? limits.MaxPartsSetting;

        var min = PlanFor(inputs.FixedContextMin);
        var max = inputs.FixedContextMax == inputs.FixedContextMin ? min : PlanFor(inputs.FixedContextMax);

        foreach (var plan in new[] { max, min }.Distinct())
        {
            if (Refusal(limits, plan, partsSetting) is { } refusal && reasons.All(r => r.Code != refusal.Code))
            {
                reasons.Add(refusal);
            }
        }

        var sizeStatus = min.IsSendable && max.IsSendable ? AiPlanStatus.WithinLimits
            : !min.IsSendable && !max.IsSendable ? AiPlanStatus.Blocked
            : AiPlanStatus.Uncertain;
        if (sizeStatus == AiPlanStatus.Uncertain)
        {
            reasons.Add(new AiPlanReason("DependsOnDocumentExcerpts", "Warning",
                "Whether this fits depends on which project document excerpts the run retrieves; that is decided by an " +
                "embedding search at run time, which this plan does not perform."));
        }

        if (operation == AiPlanOperation.Generate && limits.InputBudget is null && max.Outcome == ChunkPlanOutcome.Whole
            && limits.LargeGenerationWarningTokens is { } threshold && sourceTokens > threshold)
        {
            sizeStatus = AiPlanStatus.Worst(sizeStatus, AiPlanStatus.Warning);
            var cap = limits.MaxOutputTokens is { } outputTokens
                ? $"cut off at {limits.MaxOutputTokensSetting}={outputTokens:N0} output tokens" : "cut off at the output-token limit";
            var timeout = limits.TimeoutSeconds is { } seconds ? $" or run past the {seconds}s timeout" : string.Empty;
            reasons.Add(new AiPlanReason("LargeResponseRisk", "Warning",
                $"The whole source (~{sourceTokens:N0} tokens) goes to {limits.Provider} in one request. The requirements answer " +
                $"for a source this large may be {cap}{timeout}; if so the run fails and nothing is saved. This is a size " +
                $"heuristic (above {threshold:N0} source tokens), not a measurement.",
                Setting: limits.MaxOutputTokensSetting, Limit: limits.MaxOutputTokens, Actual: sourceTokens));
        }

        if (staging is not null && max.Outcome == ChunkPlanOutcome.Split)
        {
            reasons.Add(new AiPlanReason("OutputStaged", "Info",
                $"Requirements are generated in {max.Chunks.Count} parts of at most ~{max.MaxPartSourceTokens:N0} source tokens " +
                $"({staging.PartSourceSetting}={staging.MaxPartSourceTokens:N0}) so no answer is cut off at {limits.Provider}'s output limit. " +
                $"A part whose answer is still cut off is split again (at most {staging.MaxPartsSetting}={staging.MaxParts} parts in total), " +
                "so a run can send more requests than planned here.",
                Setting: staging.PartSourceSetting, Limit: staging.MaxPartSourceTokens, Actual: sourceTokens));
        }

        if (limits.IsStub)
        {
            sizeStatus = AiPlanStatus.Worst(sizeStatus, AiPlanStatus.Warning);
        }

        var status = cooldownUntil is not null ? AiPlanStatus.Blocked : sizeStatus;
        var parts = max.Outcome is ChunkPlanOutcome.CannotSplit || min.Outcome is ChunkPlanOutcome.CannotSplit
            ? null
            : new AiPlanRange(Math.Min(min.PartCount, max.PartCount), Math.Max(min.PartCount, max.PartCount));
        return Provider(role, limits, status, availability, cooldownUntil, reasons, max.Outcome == ChunkPlanOutcome.Whole && min.Outcome == ChunkPlanOutcome.Whole,
            parts, max.EstimatedWholeTokens, max) with { SizeStatus = sizeStatus };
    }

    private static AiPlanProvider Provider(
        string role, AiProviderLimits limits, string status, string availability, DateTimeOffset? cooldownUntil,
        List<AiPlanReason> reasons, bool whole, AiPlanRange? parts, int estimatedTokens, ChunkPlan? plan)
    {
        var split = plan is { Outcome: not ChunkPlanOutcome.Whole };
        long? total = parts is null ? null
            : whole ? estimatedTokens
            : limits.MaxRequestTokens is { } maxRequest ? (long)parts.Max * maxRequest
            : null;
        return new AiPlanProvider(
            role, limits.Provider, limits.Model, status, status, availability, cooldownUntil,
            whole, parts, estimatedTokens, limits.InputBudget, limits.MaxRequestTokens,
            split && plan!.Outcome != ChunkPlanOutcome.CannotSplit ? plan.MaxPartSourceTokens : null,
            split ? plan!.ReservedTokensPerPart : null,
            !split || plan!.SharedContextItems == 0 ? null : plan.SharedContextWhole ? "Whole" : "Subset",
            limits.MaxParts, limits.MaxPartsSetting, total, limits.MaxOutputTokens, limits.TimeoutSeconds,
            reasons.OrderBy(r => r.Severity switch { "Blocking" => 0, "Warning" => 1, _ => 2 }).ToList());
    }

    // The refusal a real run would raise for this plan, worded for before the run.
    private static AiPlanReason? Refusal(AiProviderLimits limits, ChunkPlan plan, string? partsSetting) => plan.Outcome switch
    {
        ChunkPlanOutcome.TooManyParts => new AiPlanReason("TooManyParts", "Blocking",
            $"{limits.Provider} would need {plan.Chunks.Count} parts (~{plan.MaxPartSourceTokens:N0} source tokens each within its " +
            $"{plan.InputBudget:N0}-token input budget); at most {plan.MaxParts} are allowed ({partsSetting}={plan.MaxParts}). " +
            $"A run served by {limits.Provider} is refused before any request is sent.",
            partsSetting, plan.MaxParts, plan.Chunks.Count),
        ChunkPlanOutcome.CannotSplit => new AiPlanReason("CannotSplit", "Blocking",
            $"{limits.Provider}'s {plan.InputBudget:N0}-token input budget leaves too little room for source text next to the " +
            $"~{plan.ReservedTokensPerPart:N0} tokens every part must carry (instructions, context and a repair reserve).",
            limits.BudgetSetting, plan.InputBudget, plan.ReservedTokensPerPart),
        ChunkPlanOutcome.SharedContextDoesNotFit => new AiPlanReason("ClarificationsDoNotFit", "Blocking",
            $"{plan.UnplacedSharedItems:N0} of the {plan.SharedContextItems:N0} clarifications would not fit in any of the " +
            $"{plan.Chunks.Count} parts' {plan.InputBudget:N0}-token input budget, so {limits.Provider} could not be shown them.",
            limits.BudgetSetting, plan.InputBudget, plan.UnplacedSharedItems),
        _ => null
    };

    private static AiPlanOverall Overall(AiRouteSnapshot route, IReadOnlyList<AiPlanProvider> providers)
    {
        if (route.ConfigurationError is { } error || providers.Count == 0)
        {
            return new AiPlanOverall(AiPlanStatus.Blocked, $"No AI provider can be used: {route.ConfigurationError ?? "none is configured."}");
        }

        var primary = providers[0];
        var fallback = providers.Count > 1 ? providers[1] : null;
        if (fallback is null)
        {
            return new AiPlanOverall(primary.SizeStatus, Describe(primary) + QuotaNote(primary));
        }

        if (primary.Availability == AiPlanAvailability.CoolingDown)
        {
            return new AiPlanOverall(fallback.SizeStatus,
                $"{primary.Provider} is skipped until {primary.CooldownUntil:u}, so a run now goes to {fallback.Provider}: " +
                Describe(fallback) + QuotaNote(fallback));
        }

        // A source over the primary's own budget is split for the primary; it never falls back.
        if (primary.SizeStatus == AiPlanStatus.Blocked)
        {
            return new AiPlanOverall(AiPlanStatus.Blocked, Describe(primary));
        }

        if (AiPlanStatus.Rank(fallback.SizeStatus) >= AiPlanStatus.Rank(AiPlanStatus.Uncertain))
        {
            var fallbackIssue = fallback.SizeStatus == AiPlanStatus.Blocked ? "cannot take it" : "may not be able to take it";
            return new AiPlanOverall(AiPlanStatus.Worst(primary.SizeStatus, AiPlanStatus.Warning),
                $"{primary.Provider} is tried first: {Describe(primary)} {primary.Provider}'s quota is unknown until the request is sent. " +
                $"If {primary.Provider} is rate-limited or out of quota, the fallback {fallback.Provider} {fallbackIssue}: {Describe(fallback)}");
        }

        return new AiPlanOverall(AiPlanStatus.Worst(primary.SizeStatus, fallback.SizeStatus),
            $"{primary.Provider} is tried first: {Describe(primary)} If it is rate-limited or out of quota, {fallback.Provider} " +
            $"takes over: {Describe(fallback)} Neither provider's remaining quota is known until a request is sent.");
    }

    private static string Describe(AiPlanProvider p)
    {
        if (p.Reasons.FirstOrDefault(r => r.Severity == "Blocking" && r.Code != "CoolingDown") is { } blocking)
        {
            return blocking.Message;
        }

        var size = p.Parts is null ? "request size unknown."
            : p.WholeRequest ? $"one whole request (~{p.EstimatedRequestTokens:N0} input tokens)."
            : p.Parts.Exact ? $"{p.Parts.Max} parts."
            : $"{p.Parts.Min}-{p.Parts.Max} parts, depending on the document excerpts retrieved.";
        var warning = p.Reasons.FirstOrDefault(r => r.Severity == "Warning");
        return warning is null ? size : $"{size} {warning.Message}";
    }

    private static string QuotaNote(AiPlanProvider p) => p.Availability == AiPlanAvailability.QuotaUnknown
        ? $" {p.Provider}'s remaining quota is unknown until the request is sent."
        : string.Empty;

    private static List<string> Caveats(AiPlanOperation operation, AiPlanContext context, AiRouteSnapshot route)
    {
        var caveats = new List<string>
        {
            "No AI provider was contacted to make this plan. Remaining provider quota is unknown until a request is sent.",
            "Token figures are estimates (about 3 characters per token, deliberately pessimistic); part counts use the same " +
            "splitting code as a real run."
        };
        if (route.Fallback is not null)
        {
            caveats.Add("Provider cool-downs are recorded by this server process only and are forgotten on restart.");
        }

        if (operation == AiPlanOperation.Generate && !context.FixedContextTokens.Exact)
        {
            caveats.Add($"Project document excerpts are chosen by an embedding search at run time. Figures are shown from none " +
                $"to the {context.DocumentExcerptCandidates} largest candidates.");
        }

        if (operation == AiPlanOperation.Generate && context.ClarificationItems > 0)
        {
            caveats.Add("Every clarification is shown to the AI, so answering or adding clarifications changes this plan.");
        }

        return caveats;
    }
}
