using System.Net;

namespace AiReap.Application.Ai;

// Refused locally, never sent (P0-5): a later pipeline stage - user stories, acceptance criteria,
// business rules, quality or conflict review, design, data, API, tasks, test cases - whose request
// does not fit the input budget of the provider serving it. These stages send their prompt whole or
// not at all (GenerationSupport.CallAiAndParseLosslessAsync), so nothing is condensed away. Still a
// RequestRejected AiProviderException, so retry and fallback treat it as before; the API tells it
// apart from a request the provider itself rejected.
//
// RepairAttempt: the refused request was the one automatic repair attempt, so the first request of
// the stage WAS sent (its response failed validation); only the repair was not.
//
// The message is built from the stage name, provider and token figures only - never prompt or source
// text - because it reaches the API response, the logs and the agent-run stage error.
public sealed class AiStageOverBudgetException(string stage, bool repairAttempt, AiRequestOverBudgetException overBudget)
    : AiProviderException(AiFailureKind.RequestRejected, overBudget.Provider, BuildMessage(stage, repairAttempt, overBudget),
        HttpStatusCode.RequestEntityTooLarge, BuildMessage(stage, repairAttempt, overBudget), inner: overBudget)
{
    // The stage's operationType, as the AI audit trail records it (e.g. "UserStoryGeneration").
    public string Stage { get; } = stage;

    public bool RepairAttempt { get; } = repairAttempt;

    public int EstimatedInputTokens { get; } = overBudget.EstimatedTokens;

    // Input tokens one request to the provider may carry (output reserve already set aside).
    public int InputBudget { get; } = overBudget.InputBudget;

    public int OverBudgetTokens { get; } = Math.Max(0, overBudget.EstimatedTokens - overBudget.InputBudget);

    // Why the primary provider did not serve the call, when this provider was its fallback.
    public PrimaryProviderOutcome? Primary => (InnerException as AiRequestOverBudgetException)?.Primary;

    private static string BuildMessage(string stage, bool repairAttempt, AiRequestOverBudgetException ex)
    {
        var figures = $"~{ex.EstimatedTokens:N0} estimated input tokens against the {ex.InputBudget:N0}-token input budget of " +
            $"{ex.Provider}, ~{Math.Max(0, ex.EstimatedTokens - ex.InputBudget):N0} over";
        return repairAttempt
            ? $"{stage}: the first request was sent to the AI provider but its response failed validation, and the automatic " +
              $"repair attempt is too large to send whole ({figures}), so the repair attempt was not sent. This stage never " +
              "shortens its input. Nothing was saved."
            : $"{stage}: the request is too large to send whole ({figures}), so it was not sent. This stage never shortens " +
              "its input. Nothing was saved.";
    }
}
