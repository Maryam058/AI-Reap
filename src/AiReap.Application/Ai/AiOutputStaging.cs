namespace AiReap.Application.Ai;

// Optional capability of an IAiChatClient whose responses have a hard output ceiling that a large
// requirement source can exceed (Claude: max_tokens, thinking included). Like IUncondensedAiChatClient
// it is read off the client, so the dry-run AI plan and a real run use the same limits.
public interface IStagedOutputAiChatClient : IAiChatClient
{
    // Null: this client does not stage output (requests are sent as before).
    AiOutputStaging? OutputStaging { get; }
}

// Staged requirement generation (GenerationSupport.CallAiAndParseChunkedAsync with stageOutput):
// a source estimated above MaxPartSourceTokens is generated in parts of at most that much source text,
// so each response stays well inside the output limit; a part whose response is still truncated is split
// again, never accepted. At most MaxParts parts in total. Token figures are in the units of the offline
// estimate (PromptBudgetReducer.EstimateTokens). InputBudget and MaxRequestTokens are the client's own
// per-request limits (RequestBudgetAiChatClient), used to plan parts and report refusals.
public sealed record AiOutputStaging(
    string Provider,
    int InputBudget,
    int MaxRequestTokens,
    int MaxPartSourceTokens,
    int MaxParts,
    string PartSourceSetting,
    string MaxPartsSetting);

// Implemented by a part response that can say how many items it produced and what each one claims to
// cover, so staged generation (StagedPartCoverage) can refuse a part that defines requirement IDs yet
// produced nothing (its requirements were left out) instead of saving the other parts as if the run
// were complete.
public interface IStagedPartResponse
{
    int ItemCount { get; }

    // One entry per generated requirement: the IDs it cites in sourceIds and its "srs" evidence quotes.
    IReadOnlyList<StagedCoverageClaim> CoverageClaims { get; }
}

// What one generated requirement says it comes from. A cited ID counts as covered only when one of
// the quotes is verified inside that ID's own passage (StagedPartCoverage) - a citation alone is not.
public sealed record StagedCoverageClaim(IReadOnlyList<string> SourceIds, IReadOnlyList<string> SourceQuotes);
