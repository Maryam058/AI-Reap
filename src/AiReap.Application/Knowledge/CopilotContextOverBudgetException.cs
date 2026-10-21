using System.Net;
using AiReap.Application.Ai;

namespace AiReap.Application.Knowledge;

// Why a Project Copilot request was refused for its size (CopilotService). In every case the refused
// request was not sent.
public enum CopilotRefusalReason
{
    // The question, the system instructions and the required context (structured facts, section
    // headers, impact summaries) do not fit the serving provider's input budget even with every
    // optional context item left out. The question is never shortened, so nothing was sent.
    RequiredContextTooLarge,

    // The request did not fit, and the shortened request would go through a route with another AI
    // provider (a fallback, or a primary tried or skipped first), which could then receive the
    // shortened prompt - the route cannot be pinned to the refusing provider. No second request was made.
    RetryNotSafe,

    // The first request was sent and answered, its response failed validation, and the automatic
    // repair attempt does not fit the budget, so the repair attempt was not sent.
    RepairAttempt
}

// Refused locally, never sent: a Copilot request the input budget of the provider serving it cannot
// take. Unlike AiStageOverBudgetException (stages that never shorten their input), the Copilot does
// shorten optional project context to fit - so its refusals say what was and was not tried. Still a
// RequestRejected AiProviderException, so it maps to 502 ai_request_rejected like the stage refusal.
// The message is built from the reason, provider and token figures only - never the question, prompt
// or project text.
public sealed class CopilotContextOverBudgetException(CopilotRefusalReason reason, AiRequestOverBudgetException overBudget)
    : AiProviderException(AiFailureKind.RequestRejected, overBudget.Provider, BuildMessage(reason, overBudget),
        HttpStatusCode.RequestEntityTooLarge, BuildMessage(reason, overBudget), inner: overBudget)
{
    // The Copilot's operationType, as the AI audit trail records it.
    public string Stage => CopilotService.OperationType;

    public CopilotRefusalReason Reason { get; } = reason;

    public int EstimatedInputTokens { get; } = overBudget.EstimatedTokens;

    // Input tokens one request to the provider may carry (output reserve already set aside).
    public int InputBudget { get; } = overBudget.InputBudget;

    public int OverBudgetTokens { get; } = Math.Max(0, overBudget.EstimatedTokens - overBudget.InputBudget);

    // Why the primary provider did not serve the call, when this provider was its fallback.
    public PrimaryProviderOutcome? Primary => (InnerException as AiRequestOverBudgetException)?.Primary;

    private static string BuildMessage(CopilotRefusalReason reason, AiRequestOverBudgetException ex)
    {
        var figures = $"~{ex.EstimatedTokens:N0} estimated input tokens against the {ex.InputBudget:N0}-token input budget of " +
            $"{ex.Provider}, ~{Math.Max(0, ex.EstimatedTokens - ex.InputBudget):N0} over";
        return reason switch
        {
            CopilotRefusalReason.RequiredContextTooLarge =>
                $"{CopilotService.OperationType}: the question and the context it requires do not fit even with all optional " +
                $"project context left out ({figures}), so the request was not sent. The question is never shortened. Nothing was saved.",
            CopilotRefusalReason.RetryNotSafe =>
                $"{CopilotService.OperationType}: the request is too large for {ex.Provider} ({figures}), so it was not sent. " +
                "The Copilot shortens optional project context only when the shortened request can reach no other AI provider; " +
                "this route has another provider, so no shortened request was made. Nothing was saved.",
            _ =>
                $"{CopilotService.OperationType}: the first request was sent to the AI provider but its response failed validation, " +
                $"and the automatic repair attempt is too large to send ({figures}), so the repair attempt was not sent. Nothing was saved."
        };
    }
}
