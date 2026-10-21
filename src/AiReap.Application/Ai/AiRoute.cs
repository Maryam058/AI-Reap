namespace AiReap.Application.Ai;

// What the configured IAiChatClient would do with a request, read from the client itself (never by
// calling a provider), for the dry-run AI plan (AiPlanService).
public interface IAiRouteInspector
{
    AiRouteSnapshot Inspect();
}

// Primary is the provider tried first; Fallback (if any) serves when the primary is rate-limited,
// out of quota, unavailable or timing out. PrimaryCooldownUntil is set only while this server
// process has the primary in a recorded cool-down; null does not mean the primary has quota.
// ConfigurationError: the AI client could not be resolved at all (e.g. an unknown Ai:Provider).
public sealed record AiRouteSnapshot(
    AiProviderLimits? Primary,
    AiProviderLimits? Fallback,
    DateTimeOffset? PrimaryCooldownUntil,
    string? ConfigurationError = null);

// One provider's request limits as its client enforces them.
// InputBudget null: no per-request budget - a request is always sent whole (Gemini).
// MaxParts 0: no limit on parts. LimitsKnown false: an unrecognized client, so its limits cannot be
// read and the plan for it is uncertain. LargeGenerationWarningTokens: above this source size, a
// whole-source generation request is flagged as at risk of output truncation or timeout.
public sealed record AiProviderLimits(
    string Provider,
    string Model,
    bool IsStub,
    bool LimitsKnown,
    int? InputBudget,
    int? MaxRequestTokens,
    string? BudgetSetting,
    int MaxParts,
    string? MaxPartsSetting,
    int? MaxOutputTokens,
    string? MaxOutputTokensSetting,
    int? TimeoutSeconds,
    int? LargeGenerationWarningTokens);
