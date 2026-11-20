namespace AiReap.Application.Ai.Pipeline;

public enum AiPlanOperation
{
    Analyze,
    Generate
}

// Statuses, most to least favourable. "WithinLimits" is about request size only - it never means a
// provider has quota left, which cannot be known without calling it.
public static class AiPlanStatus
{
    public const string WithinLimits = "WithinLimits";
    public const string Warning = "Warning";
    public const string Uncertain = "Uncertain";
    public const string Blocked = "Blocked";

    public static int Rank(string status) => status switch
    {
        WithinLimits => 0,
        Warning => 1,
        Uncertain => 2,
        _ => 3
    };

    public static string Worst(params string[] statuses) => statuses.OrderByDescending(Rank).First();
}

// What is known about whether a provider can take a request right now. Never "available": quota is
// only learnt by sending a request.
public static class AiPlanAvailability
{
    public const string QuotaUnknown = "QuotaUnknown";
    public const string CoolingDown = "CoolingDown";
    public const string Stub = "Stub";
    public const string NotConfigured = "NotConfigured";
}

// The dry-run AI plan for one operation on one requirement source: how each configured provider
// would take it, computed with the same chunk planning as a real run (ChunkPlanner), without
// calling any AI or embedding provider and without writing anything. Token figures are estimates.
public sealed record AiPlanResult(
    string Operation,
    Guid RequirementSourceId,
    DateTime GeneratedAt,
    AiPlanContext Context,
    AiPlanRoute Route,
    IReadOnlyList<AiPlanProvider> Providers,
    AiPlanOverall Overall,
    IReadOnlyList<string> Caveats,
    AiPlanBatch? Batch = null);

// Batch mode (AiChunkRunService). Enabled false: Ai:Groq:MaxBatchedRunParts = 0, nothing else is
// filled and no batched-run table was read. Parts: how many parts a batched run would have, sized
// to BudgetProvider's per-request budget (for generation, with the largest candidate document
// excerpts - Start retrieves the real ones). CanStart: a run could be started now; otherwise Reason.
public sealed record AiPlanBatch(
    bool Enabled,
    int MaxRunParts,
    string? BudgetProvider,
    int? PartsPerAction,
    int? Parts,
    int? Actions,
    bool CanStart,
    string? Reason,
    AiChunkRunResponse? ActiveRun);

// What every request carries besides the source. Fixed context (objectives + document excerpts)
// is a range when excerpts may be included: which excerpts a real run retrieves depends on an
// embedding similarity search the plan does not run, so Min assumes none and Max the largest.
public sealed record AiPlanContext(
    int SourceTokens,
    int SystemPromptTokens,
    AiPlanRange FixedContextTokens,
    int ClarificationItems,
    int ClarificationTokens,
    int BusinessObjectives,
    int DocumentExcerptCandidates);

public sealed record AiPlanRange(int Min, int Max)
{
    public bool Exact => Min == Max;
}

// Primary is tried first; a run goes to Fallback while the primary is cooling down, or after the
// primary fails with a rate limit, quota, outage or timeout. ExpectedFirst: the provider a run
// started now would try first. CooldownTracked: cool-downs are recorded per server process only.
public sealed record AiPlanRoute(
    string? Primary,
    string? Fallback,
    DateTimeOffset? PrimaryCooldownUntil,
    string? ExpectedFirst,
    string? ConfigurationError);

public sealed record AiPlanProvider(
    string Role,
    string Provider,
    string Model,
    string Status,
    string SizeStatus,
    string Availability,
    DateTimeOffset? CooldownUntil,
    bool WholeRequest,
    AiPlanRange? Parts,
    int EstimatedRequestTokens,
    int? InputBudget,
    int? MaxRequestTokens,
    int? MaxPartSourceTokens,
    int? ReservedTokensPerPart,
    string? ClarificationMode,
    int MaxParts,
    string? MaxPartsSetting,
    long? EstimatedTotalTokensUpperBound,
    int? MaxOutputTokens,
    int? TimeoutSeconds,
    IReadOnlyList<AiPlanReason> Reasons);

// Severity: "Blocking" (a run would be refused or skip this provider), "Warning" (may fail or
// degrade), "Info".
public sealed record AiPlanReason(
    string Code,
    string Severity,
    string Message,
    string? Setting = null,
    int? Limit = null,
    int? Actual = null,
    DateTimeOffset? Until = null);

public sealed record AiPlanOverall(string Status, string Summary);
