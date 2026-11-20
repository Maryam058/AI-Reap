using System.Text.RegularExpressions;

namespace AiReap.Application.Ai.Pipeline;

// How GenerationSupport.CallAiAndParseChunkedAsync would send a requirement source: whole, or split
// into parts sized to the serving provider's per-request input budget - or refused before anything
// is sent. Pure: no AI call, no database, no side effect. The real chunked call and the dry-run AI
// plan (AiPlanService) both use it, so a plan's part count is the count a real run sends.
public static class ChunkPlanner
{
    // The rule RequestBudgetAiChatClient.CompleteUncondensedAsync applies: lossless clean-up is
    // allowed, dropping any passage is not.
    public static bool SendsWhole(PromptReduction fit) => fit.Fits && fit.OmittedPassages == 0;

    // The prompt a whole-source request carries (CallAiAndParseChunkedAsync's first attempt).
    public static string WholePrompt(string sourceText, GenerationSupport.SharedContext shared, string fixedContext) =>
        sourceText + shared.RenderAll() + fixedContext;

    // The full decision for a provider: inputBudget null means the provider has no per-request
    // budget (Gemini), so the source always goes whole. maxParts 0 means no part limit.
    public static ChunkPlan Plan(
        string systemPrompt, string sourceText, string fixedContext, GenerationSupport.SharedContext shared,
        int? inputBudget, int maxParts)
    {
        var whole = WholePrompt(sourceText, shared, fixedContext);
        if (inputBudget is not { } budget)
        {
            return ChunkPlan.Whole(PromptBudgetReducer.EstimateRequestTokens(systemPrompt, whole), null);
        }

        var fit = PromptBudgetReducer.Fit(systemPrompt, whole, budget);
        return SendsWhole(fit)
            ? ChunkPlan.Whole(fit.OriginalTokens, budget)
            : Split(systemPrompt, sourceText, fixedContext, shared, budget, maxParts, fit.OriginalTokens);
    }

    // Staged generation's plan (AiOutputStaging): whole only when the source is within the staging
    // part size AND the whole prompt fits the input budget; otherwise split into parts of at most
    // MaxPartSourceTokens of source, at most MaxParts parts. The real run (GenerationSupport) and the
    // dry-run AI plan (AiPlanService) both call this, so they agree on the parts.
    public static ChunkPlan PlanStaged(
        string systemPrompt, string sourceText, string fixedContext, GenerationSupport.SharedContext shared, AiOutputStaging staging)
    {
        var fit = PromptBudgetReducer.Fit(systemPrompt, WholePrompt(sourceText, shared, fixedContext), staging.InputBudget);
        return PromptBudgetReducer.EstimateTokens(sourceText) <= staging.MaxPartSourceTokens && SendsWhole(fit)
            ? ChunkPlan.Whole(fit.OriginalTokens, staging.InputBudget)
            : Split(systemPrompt, sourceText, fixedContext, shared, staging.InputBudget, staging.MaxParts, fit.OriginalTokens,
                staging.MaxPartSourceTokens);
    }

    // The split once the whole request is known not to fit the budget. Each part request carries
    // the system prompt, request framing, the part header, fixedContext, its share of the shared
    // context and a repair reserve; the rest of the budget is source text. Refusals are checked in
    // the order the real call applies them.
    //
    // maxPartSourceTokens (staged generation, AiOutputStaging): an upper bound on each part's source
    // text below what the input budget alone would allow, so each part's response fits the output limit.
    public static ChunkPlan Split(
        string systemPrompt, string sourceText, string fixedContext, GenerationSupport.SharedContext shared,
        int inputBudget, int maxParts, int estimatedWholeTokens, int? maxPartSourceTokens = null)
    {
        var reserved = PromptBudgetReducer.EstimateTokens(systemPrompt) + PromptBudgetReducer.RequestOverheadTokens
            + PromptBudgetReducer.EstimateTokens(GenerationSupport.PartHeader(999, 999)) + PromptBudgetReducer.EstimateTokens(fixedContext)
            + GenerationSupport.PartRepairReserveTokens;

        // The shared list goes whole with every part while it takes at most its share of what is
        // left; beyond that, each part carries a bounded, relevance-chosen subset of it.
        var available = inputBudget - reserved;
        var sharedTokens = shared.Items.Count == 0 ? 0
            : PromptBudgetReducer.EstimateTokens(shared.Heading) + shared.Items.Sum(PromptBudgetReducer.EstimateTokens);
        var sharedWhole = sharedTokens <= available / GenerationSupport.SharedContextBudgetDivisor;
        var sharedBudget = sharedWhole ? sharedTokens : available / GenerationSupport.SharedContextBudgetDivisor;
        var maxPartTokens = available - sharedBudget;
        if (maxPartSourceTokens is { } cap && cap < maxPartTokens)
        {
            maxPartTokens = cap;
        }

        var plan = new ChunkPlan(ChunkPlanOutcome.Split, estimatedWholeTokens, inputBudget, maxParts, reserved,
            shared.Items.Count, sharedTokens, sharedWhole, sharedBudget, maxPartTokens, [], [], [], 0);

        if (maxPartTokens < GenerationSupport.MinPartSourceTokens)
        {
            return plan with { Outcome = ChunkPlanOutcome.CannotSplit };
        }

        var chunks = SourceChunker.Split(sourceText, maxPartTokens);
        if (maxParts > 0 && chunks.Count > maxParts)
        {
            return plan with { Outcome = ChunkPlanOutcome.TooManyParts, Chunks = chunks };
        }

        if (sharedWhole)
        {
            return plan with
            {
                Chunks = chunks,
                SharedContextParts = chunks.Select(_ => shared.RenderAll()).ToList(),
                SharedItemsPerPart = chunks.Select(_ => shared.Items.Count).ToList()
            };
        }

        var placement = PlaceSharedContext(shared, chunks, sharedBudget);
        return plan with
        {
            Outcome = placement.Unplaced > 0 ? ChunkPlanOutcome.SharedContextDoesNotFit : ChunkPlanOutcome.Split,
            Chunks = chunks,
            SharedContextParts = placement.Parts,
            SharedItemsPerPart = placement.ItemsPerPart,
            UnplacedSharedItems = placement.Unplaced,
            SharedContextItemBudget = placement.ItemBudget
        };
    }

    private const string SharedContextSubsetNote =
        "[AI-REAP note: {0} of the {1} entries of this list are shown with this part - those most relevant to it; " +
        "the others are shown with the other parts.]\n";

    private sealed record SharedPlacement(List<string> Parts, List<int> ItemsPerPart, int Unplaced, int ItemBudget);

    // Each part's rendering of a shared list too large to go whole with every part. First every
    // item is placed with the part it is most relevant to (terms shared with that part's text) that
    // still has room, so each item is shown at least once; then each part's remaining room is
    // filled with other items relevant to it. Items keep their original order within a part. An
    // item that fits no part is counted as unplaced: the caller refuses rather than drop it.
    private static SharedPlacement PlaceSharedContext(
        GenerationSupport.SharedContext shared, IReadOnlyList<string> chunks, int sharedBudget)
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
            return new SharedPlacement([], [], unplaced, itemBudget);
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

        var parts = selected.Select(s => s.Count == 0 ? string.Empty
            : shared.Heading + string.Format(SharedContextSubsetNote, s.Count, shared.Items.Count) + string.Concat(s.Select(i => shared.Items[i])))
            .ToList();
        return new SharedPlacement(parts, selected.Select(s => s.Count).ToList(), 0, itemBudget);
    }

    private static readonly Regex ContextWord =
        new(@"\b[A-Za-z][A-Za-z-]{3,}\b", RegexOptions.Compiled);

    private static HashSet<string> ContextTerms(string text) =>
        ContextWord.Matches(text).Select(m => m.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
}

public enum ChunkPlanOutcome
{
    // One request carries the whole source (no budget, or it fits).
    Whole,

    // Split into Chunks, each sendable.
    Split,

    // The budget leaves less than GenerationSupport.MinPartSourceTokens for source text per part.
    CannotSplit,

    // More parts than MaxParts.
    TooManyParts,

    // Some shared-context items (clarifications) fit no part.
    SharedContextDoesNotFit
}

// InputBudget null: no per-request budget. MaxParts 0: no part limit. The split figures are 0/empty
// for a Whole plan; Chunks is filled for Split, TooManyParts and SharedContextDoesNotFit.
public sealed record ChunkPlan(
    ChunkPlanOutcome Outcome,
    int EstimatedWholeTokens,
    int? InputBudget,
    int MaxParts,
    int ReservedTokensPerPart,
    int SharedContextItems,
    int SharedContextTokens,
    bool SharedContextWhole,
    int SharedContextBudget,
    int MaxPartSourceTokens,
    IReadOnlyList<string> Chunks,
    IReadOnlyList<string> SharedContextParts,
    IReadOnlyList<int> SharedItemsPerPart,
    int UnplacedSharedItems)
{
    public int SharedContextItemBudget { get; init; }

    public static ChunkPlan Whole(int estimatedTokens, int? inputBudget) =>
        new(ChunkPlanOutcome.Whole, estimatedTokens, inputBudget, 0, 0, 0, 0, true, 0, 0, [], [], [], 0);

    public int PartCount => Outcome == ChunkPlanOutcome.Whole ? 1 : Chunks.Count;

    public bool IsSendable => Outcome is ChunkPlanOutcome.Whole or ChunkPlanOutcome.Split;
}
