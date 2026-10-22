namespace AiReap.Infrastructure.Ai;

// Anthropic Claude (Messages API) settings - the only chat provider (see docs/CLAUDE_SETUP.md). The
// key is never stored in appsettings*.json: supply it via `dotnet user-secrets set "Ai:Anthropic:ApiKey" ...`
// in development or the Ai__Anthropic__ApiKey environment variable elsewhere. The conventional
// ANTHROPIC_API_KEY variable is deliberately NOT read: a developer machine that has it set for other
// tools would otherwise make Development send real, billed requests instead of using the stub.
public class AnthropicOptions
{
    public const string SectionName = "Ai:Anthropic";

    public const string DefaultModel = "claude-sonnet-5-5";

    public string ApiKey { get; set; } = string.Empty;

    // An explicitly configured model (appsettings, user-secrets, Ai__Anthropic__Model) always wins and
    // is sent and recorded in the AI audit trail exactly as configured.
    public string Model { get; set; } = DefaultModel;

    public string BaseUrl { get; set; } = "https://api.anthropic.com/";

    // max_tokens: the per-response output ceiling. With adaptive thinking (on by default for Claude
    // Sonnet 5.5) thinking tokens are part of this budget too. A response that reaches it is cut off
    // (stop_reason "max_tokens") and rejected as truncated - never accepted as complete. 16000 keeps
    // a non-streaming request well inside a 10-minute window; raise it (and TimeoutSeconds) for very
    // long generations. The model's own ceiling is 128K.
    public int MaxTokens { get; set; } = 16000;

    // Application-side cap on one request, input plus MaxTokens, in Claude tokens. Deliberately far
    // below the model's 1M context window: per-request cost and latency stay bounded, and long-context
    // recall degrades as prompts grow. A source over the input budget is never shortened: requirement
    // analysis/generation split it losslessly into parts (GenerationSupport.CallAiAndParseChunkedAsync)
    // and every later stage refuses it before sending (AiStageOverBudgetException).
    public int MaxRequestTokens { get; set; } = 200_000;

    // The model's context window (input + output), used only to validate MaxRequestTokens.
    public int ContextWindowTokens { get; set; } = 1_000_000;

    // The input budget is enforced with the application's offline estimate (PromptBudgetReducer, about
    // 3 characters per token). Claude's current tokenizer averages about 2.5 characters per token
    // (1M tokens ~ 2.5M characters per Anthropic's documentation), so the estimate is scaled by
    // EstimatedCharsPerToken / 3 to stay conservative. Lower it for unusually token-dense text.
    public double EstimatedCharsPerToken { get; set; } = 2.5;

    // The most parts a requirement source split for Claude may be sent in; 0 disables the check.
    public int MaxChunkedParts { get; set; } = 8;

    // Staged requirement generation (AiOutputStaging). Requirement generation answers with roughly
    // 2-5x as many tokens as the source text it covers (each requirement carries fields, evidence quotes
    // and IDs; ~5x on the requirement-dense golden fixture), and thinking shares MaxTokens, so a large
    // source cannot be answered in one response. A source above this many source tokens (offline estimate
    // units, ~3 characters each; 6000 is ~18,000 characters, a few pages) is generated in parts of at most
    // this size; a part whose answer is still cut off is split again. 0 turns staging off (one response).
    public int GenerationPartSourceTokens { get; set; } = 6000;

    // The most parts staged generation may use in total, including re-splits after truncation. Every
    // part is a separate, sequential, billed request (~1-2 minutes each), so this also bounds cost and
    // latency; a source needing more is refused before anything is sent.
    public int MaxGenerationParts { get; set; } = 24;

    // Per-attempt timeout. A large source with a long JSON answer plus thinking can legitimately run
    // for minutes. A timed-out request had usually already been received and may still be processed
    // and BILLED by Anthropic; that is why MaxTimeoutAttempts defaults to 1 (no automatic resend).
    public int TimeoutSeconds { get; set; } = 600;

    public int MaxTimeoutAttempts { get; set; } = 1;

    // output_config.effort ("low", "medium", "high", "xhigh", "max"). Null omits it, so the model's
    // default applies (high for Claude Sonnet 5.5).
    public string? Effort { get; set; }

    // Opt-in: when the offline estimate says a request is over the input budget, ask Anthropic's
    // POST /v1/messages/count_tokens for the real count before splitting or refusing. Free, but a
    // separate network call with its own rate limit, and itself an estimate (it may include
    // system-added tokens that are not billed). Off by default so the dry-run AI plan, which never
    // calls a provider, always matches what a real run does. If the count call fails, the offline
    // estimate decides.
    public bool UseTokenCountingApi { get; set; }

    // A blank ApiKey is an obvious "not configured" signal, but a leftover placeholder (e.g. the
    // literal "sk-ant-..." some setup docs show as an example) is not blank and would otherwise
    // pass a null/whitespace check straight through to a real HTTP call that's guaranteed to fail
    // with 401. Real Anthropic keys are long (100+ characters) and never contain "..." - anything
    // shorter or containing it is treated as unconfigured.
    private const int MinimumPlausibleKeyLength = 40;

    public bool HasUsableApiKey =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !ApiKey.Contains("...", StringComparison.Ordinal)
        && ApiKey.Trim().Length >= MinimumPlausibleKeyLength;

    // Input tokens one request may carry, in Claude tokens.
    public int InputTokenBudget => Math.Max(0, MaxRequestTokens - Math.Max(0, MaxTokens));

    // The same budget in the units of the offline estimate (PromptBudgetReducer.EstimateTokens).
    public int EstimatedInputBudget(double estimatorCharsPerToken) =>
        (int)Math.Floor(InputTokenBudget * Math.Min(1.0, EstimatedCharsPerToken / estimatorCharsPerToken));

    // Configuration problems that would make every request fail or exceed the model's limits;
    // empty when the settings are usable. Never includes the key.
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Model))
            errors.Add($"{SectionName}:Model is empty. Set it to '{DefaultModel}'.");
        if (MaxTokens < 1 || MaxTokens > 128_000)
            errors.Add($"{SectionName}:MaxTokens must be between 1 and 128000 (is {MaxTokens}).");
        if (MaxRequestTokens <= MaxTokens)
            errors.Add($"{SectionName}:MaxRequestTokens ({MaxRequestTokens}) must be larger than {SectionName}:MaxTokens ({MaxTokens}).");
        if (ContextWindowTokens > 0 && MaxRequestTokens > ContextWindowTokens)
            errors.Add($"{SectionName}:MaxRequestTokens ({MaxRequestTokens}) exceeds the model's context window " +
                $"({SectionName}:ContextWindowTokens={ContextWindowTokens}).");
        if (EstimatedCharsPerToken is <= 0 or > 3.0)
            errors.Add($"{SectionName}:EstimatedCharsPerToken must be greater than 0 and at most 3.0 (is {EstimatedCharsPerToken}).");
        if (GenerationPartSourceTokens != 0 && (GenerationPartSourceTokens < 500 || GenerationPartSourceTokens > EstimatedInputBudget(3.0)))
            errors.Add($"{SectionName}:GenerationPartSourceTokens must be 0 (off) or between 500 and the input budget " +
                $"({EstimatedInputBudget(3.0)}) (is {GenerationPartSourceTokens}).");
        if (GenerationPartSourceTokens != 0 && MaxGenerationParts is < 1 or > 200)
            errors.Add($"{SectionName}:MaxGenerationParts must be between 1 and 200 (is {MaxGenerationParts}).");
        if (Effort is { } effort && !string.IsNullOrWhiteSpace(effort)
            && effort.Trim().ToLowerInvariant() is not ("low" or "medium" or "high" or "xhigh" or "max"))
            errors.Add($"{SectionName}:Effort must be one of low, medium, high, xhigh, max, or empty (is '{effort}').");
        return errors;
    }
}
