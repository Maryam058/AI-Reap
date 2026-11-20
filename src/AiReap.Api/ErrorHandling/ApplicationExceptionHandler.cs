using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Common;
using AiReap.Application.Lifecycle;
using AiReap.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.ErrorHandling;

// Maps the application's expected failure types to specific ProblemDetails responses instead of the
// generic 500 (GlobalExceptionHandler stays the last resort for genuine bugs). Every response
// carries a stable machine-readable "code" and a "retryable" hint for the frontend. Messages come
// from exceptions whose text is already sanitized - no API keys, no prompt text.
public sealed class ApplicationExceptionHandler(ILogger<ApplicationExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var mapped = Map(exception);
        if (mapped is null)
        {
            return false;
        }

        var (status, code, title, detail, retryable, extra) = mapped.Value;

        // A source analysed in parts (GenerationSupport.CallAiAndParseChunkedAsync): name the part
        // that failed, also where the detail above is a fixed sentence.
        if (exception.Data[GenerationSupport.FailedPartKey] is int failedPart && exception.Data[GenerationSupport.PartCountKey] is int partCount)
        {
            extra["failedPart"] = failedPart;
            extra["partCount"] = partCount;
            var label = $"Part {failedPart} of {partCount} of the requirement source";
            if (!detail.StartsWith(label, StringComparison.Ordinal))
            {
                detail = $"{label} failed; nothing was saved. {detail}";
            }
        }

        if (status >= 500)
        {
            logger.LogWarning("{Method} {Path} failed: {Code} {Detail}", httpContext.Request.Method, httpContext.Request.Path, code, detail);
        }

        var problemDetails = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path
        };
        problemDetails.Extensions["code"] = code;
        problemDetails.Extensions["retryable"] = retryable;
        foreach (var (key, value) in extra)
        {
            problemDetails.Extensions[key] = value;
        }

        if (exception is AiProviderException { RetryAfter: { } retryAfter })
        {
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }

        httpContext.Response.StatusCode = status;

        var problemDetailsService = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problemDetails
        });
    }

    private static (int Status, string Code, string Title, string Detail, bool Retryable, Dictionary<string, object?> Extra)? Map(Exception exception) =>
        exception switch
        {
            AiChunkLimitExceededException tooManyParts => MapChunkLimit(tooManyParts),
            AiSourceCannotBeSplitException cannotSplit => MapCannotSplit(cannotSplit),
            AiStageOverBudgetException stageOverBudget => MapStageOverBudget(stageOverBudget),
            Application.Knowledge.CopilotContextOverBudgetException copilotOverBudget => MapCopilotOverBudget(copilotOverBudget),
            AiProviderException ai => MapProvider(ai),

            // Staged generation left requirement IDs uncovered (GenerationSupport, StagedPartCoverage). The
            // message already says nothing was saved, how many requests were sent and when generating again
            // can help; never "retryable", since the cause may be deterministic.
            AiStagedPartIncompleteException incomplete => (
                StatusCodes.Status502BadGateway, "ai_invalid_output",
                "The AI response could not be used.",
                incomplete.Message, false,
                new Dictionary<string, object?>
                {
                    ["errors"] = incomplete.Errors.Take(10).ToArray(),
                    ["failedParts"] = incomplete.Parts.Take(10)
                        .Select(p => new { part = p.Part, partCount = p.PartCount, missingIds = p.MissingIds.Take(50).ToArray() }).ToArray(),
                    ["requestsSent"] = incomplete.RequestsSent
                }),

            AiOutputValidationException invalid => (
                StatusCodes.Status502BadGateway, "ai_invalid_output",
                "The AI response could not be used.",
                invalid.RepairAttempted
                    ? "The AI returned output that failed validation, even after one automatic correction attempt. Nothing was saved. Try again; if it keeps failing, simplify or shorten the requirement text."
                    : invalid.Repairable
                        ? $"The AI response could not be used and no automatic correction was attempted: {invalid.Message} Nothing was saved. Try again."
                        : invalid.Message,
                invalid.Repairable,
                new Dictionary<string, object?> { ["errors"] = invalid.Errors.Take(10).ToArray() }),

            InvalidArtifactStatusTransitionException transition => (
                StatusCodes.Status409Conflict, "invalid_status_transition",
                "This status change is not allowed.",
                transition.Message, false,
                new Dictionary<string, object?>
                {
                    ["from"] = transition.From.ToString(),
                    ["to"] = transition.To.ToString(),
                    ["allowed"] = ArtifactStatusTransitions.AllowedHumanTargets(transition.From).Select(s => s.ToString()).ToArray()
                }),

            RequestValidationException validation => (
                StatusCodes.Status400BadRequest, "validation_failed",
                "The request is not valid.", validation.Message, false, new Dictionary<string, object?>()),

            // §5 lifecycle: a stage's exit checks are not met; the failing checks say what is missing.
            LifecycleEvidenceException evidence => (
                StatusCodes.Status409Conflict, "lifecycle_evidence_failed",
                "The stage's exit checks are not met.", evidence.Message, false,
                new Dictionary<string, object?> { ["failingChecks"] = evidence.FailingChecks }),

            // Batched AI runs (AiChunkRunService): batch mode off, or a run that cannot be started.
            AiBatchRequestException batch => (
                StatusCodes.Status400BadRequest, batch.Code,
                batch.Code == AiBatchRequestException.BatchModeDisabled
                    ? "Batch mode is disabled."
                    : "This source cannot be processed in batches.",
                batch.Message, false, new Dictionary<string, object?>()),

            AiChunkRunActiveException active => (
                StatusCodes.Status409Conflict, "ai_batch_run_active",
                "A batched run is already active for this source.", active.Message, false,
                new Dictionary<string, object?> { ["activeRunId"] = active.ActiveRunId }),

            // Wrong state for the request: skipped stage, already decided, pending request, lost race.
            ConflictException conflict => (
                StatusCodes.Status409Conflict, "conflict",
                "The request conflicts with the current state.", conflict.Message, false, new Dictionary<string, object?>()),

            // A role / separation-of-duties rule enforced inside an application service.
            UnauthorizedAccessException forbidden => (
                StatusCodes.Status403Forbidden, "forbidden",
                "You are not allowed to do this.", forbidden.Message, false, new Dictionary<string, object?>()),

            _ => null
        };

    private static (int, string, string, string, bool, Dictionary<string, object?>) MapProvider(AiProviderException ai)
    {
        var extra = new Dictionary<string, object?> { ["provider"] = ai.Provider };
        return ai.Kind switch
        {
            AiFailureKind.NotConfigured => (StatusCodes.Status503ServiceUnavailable, "ai_not_configured",
                "The AI provider is not configured.", ai.Message, false, extra),
            AiFailureKind.Authentication => (StatusCodes.Status502BadGateway, "ai_auth_failed",
                "The AI provider rejected the configured credentials.",
                "The AI provider API key is invalid, revoked, or lacks access. An administrator must update it.", false, extra),
            AiFailureKind.RateLimited => MapRateLimit(ai, extra),
            AiFailureKind.Timeout => (StatusCodes.Status504GatewayTimeout, "ai_timeout",
                "The AI provider did not respond in time.",
                "The AI request timed out. The provider may already have received and processed it, so it may still be billed. " +
                "Try again; very long requirement text may need to be split.", true, extra),
            AiFailureKind.Unavailable => (StatusCodes.Status503ServiceUnavailable, "ai_unavailable",
                "The AI provider is temporarily unavailable.", "The AI service could not be reached. Try again shortly.", true, extra),
            AiFailureKind.ContentBlocked => (StatusCodes.Status422UnprocessableEntity, "ai_content_blocked",
                "The AI provider refused to process this content.", ai.Message, false, extra),
            _ => (StatusCodes.Status502BadGateway, "ai_request_rejected",
                "The AI provider rejected the request.", ai.ProviderMessage ?? ai.Message, false, extra)
        };
    }

    // Refused before anything was sent (GenerationSupport's MaxChunkedParts guard), so it must not read
    // as a provider rejection. Code, status and retryable stay those of ai_request_rejected for clients.
    // The detail does not claim another provider is unavailable: Groq may also be the configured
    // primary (Ai:Provider=Groq) or the only provider with a key, and an over-budget Groq call never
    // falls back.
    private static (int, string, string, string, bool, Dictionary<string, object?>) MapChunkLimit(AiChunkLimitExceededException ex) => (
        StatusCodes.Status502BadGateway, "ai_request_rejected",
        $"The requirement source is too large for {ex.Provider}.",
        PrimaryDetail(ex.Primary) +
        $"The requirement source exceeds the configured {ex.Provider} processing limit " +
        $"(it would need {ex.RequiredParts} parts; {ex.MaxPartsSetting}={ex.MaxParts}). " +
        $"No request was sent to {ex.Provider} and nothing was saved. " +
        (ex.Primary is null
            ? $"Split the requirement source into smaller parts, or ask an administrator to review {ex.MaxPartsSetting}."
            : "Retry when an AI provider without this limit is available, or split the requirement source into smaller parts."),
        false,
        new Dictionary<string, object?>
        {
            ["provider"] = ex.Provider,
            ["sentToProvider"] = false,
            ["requiredParts"] = ex.RequiredParts,
            ["maxParts"] = ex.MaxParts
        }.WithPrimary(ex.Primary));

    // Also refused before anything was sent: the fixed part of every request already fills the input
    // budget, so splitting the source cannot help. Same code, status and retryable as above.
    private static (int, string, string, string, bool, Dictionary<string, object?>) MapCannotSplit(AiSourceCannotBeSplitException ex) => (
        StatusCodes.Status502BadGateway, "ai_request_rejected",
        $"The requirement source cannot be sent to {ex.Provider}.",
        PrimaryDetail(ex.Primary) +
        $"{ex.Provider}'s configured input budget of {ex.InputBudget:N0} tokens leaves too little room for source text after " +
        $"the ~{ex.ReservedTokens:N0} tokens of instructions and context every request must carry. " +
        $"No request was sent to {ex.Provider} and nothing was saved. Splitting the requirement source will not help: " +
        (ex.Primary is null
            ? $"ask an administrator to review Ai:{ex.Provider}:MaxRequestTokens."
            : $"use an AI provider without this limit, or ask an administrator to review Ai:{ex.Provider}:MaxRequestTokens."),
        false,
        new Dictionary<string, object?>
        {
            ["provider"] = ex.Provider,
            ["sentToProvider"] = false,
            ["inputBudget"] = ex.InputBudget,
            ["reservedTokens"] = ex.ReservedTokens
        }.WithPrimary(ex.Primary));

    // P0-5 — a later pipeline stage whose request does not fit the input budget of the provider
    // serving it; these stages never shorten their input, so the request was refused before it was
    // sent. Same code, status and retryable as above. sentToProvider describes the refused request
    // only: when it was the repair attempt, the first request had been sent, and the detail says so.
    private static (int, string, string, string, bool, Dictionary<string, object?>) MapStageOverBudget(AiStageOverBudgetException ex) => (
        StatusCodes.Status502BadGateway, "ai_request_rejected",
        $"The {ex.Stage} request is too large for {ex.Provider}.",
        PrimaryDetail(ex.Primary) +
        (ex.RepairAttempt
            ? $"The first {ex.Stage} request was sent to {ex.Provider}, but its response failed validation, and the automatic " +
              $"repair attempt (~{ex.EstimatedInputTokens:N0} estimated input tokens) exceeds {ex.Provider}'s configured input budget of " +
              $"{ex.InputBudget:N0} tokens by ~{ex.OverBudgetTokens:N0}. The repair attempt was not sent and nothing was saved. "
            : $"The {ex.Stage} request (~{ex.EstimatedInputTokens:N0} estimated input tokens) exceeds {ex.Provider}'s configured input " +
              $"budget of {ex.InputBudget:N0} tokens by ~{ex.OverBudgetTokens:N0}. No request was sent to {ex.Provider} and nothing was saved. ") +
        "This stage does not shorten its input, because that could silently leave requirements out. " +
        (ex.Primary is null
            ? $"Reduce the input this stage carries (for example, split the requirement source), or ask an administrator to review Ai:{ex.Provider}:MaxRequestTokens."
            : "Retry when an AI provider without this limit is available, or reduce the input this stage carries."),
        false,
        new Dictionary<string, object?>
        {
            ["provider"] = ex.Provider,
            ["sentToProvider"] = false,
            ["stage"] = ex.Stage,
            ["estimatedInputTokens"] = ex.EstimatedInputTokens,
            ["inputBudget"] = ex.InputBudget,
            ["overBudgetTokens"] = ex.OverBudgetTokens
        }.WithPrimary(ex.Primary));

    // A Project Copilot request the serving provider's budget cannot take (CopilotService). The Copilot
    // does shorten optional project context to fit, so - unlike the stage refusal above - the detail says
    // what was tried; never the question or any project text. Same code and status as the stage
    // refusal; retryable only when the shortened request was withheld and the primary (which takes the
    // request whole) did not serve it this time.
    private static (int, string, string, string, bool, Dictionary<string, object?>) MapCopilotOverBudget(
        Application.Knowledge.CopilotContextOverBudgetException ex) => (
        StatusCodes.Status502BadGateway, "ai_request_rejected",
        $"The Copilot request is too large for {ex.Provider}.",
        PrimaryDetail(ex.Primary) + ex.Reason switch
        {
            Application.Knowledge.CopilotRefusalReason.RequiredContextTooLarge =>
                $"Your question and the project context it requires (~{ex.EstimatedInputTokens:N0} estimated input tokens) exceed " +
                $"{ex.Provider}'s configured input budget of {ex.InputBudget:N0} tokens by ~{ex.OverBudgetTokens:N0}, even with all optional " +
                $"project context left out. No request was sent to {ex.Provider} and nothing was saved. The Copilot never shortens your " +
                "question. " + (ex.Primary is null
                    ? $"Ask a shorter question, or ask an administrator to review Ai:{ex.Provider}:MaxRequestTokens."
                    : "Ask a shorter question, or retry when an AI provider without this limit is available."),
            Application.Knowledge.CopilotRefusalReason.RetryNotSafe =>
                $"The request (~{ex.EstimatedInputTokens:N0} estimated input tokens) exceeds {ex.Provider}'s configured input budget of " +
                $"{ex.InputBudget:N0} tokens, so it was not sent. The Copilot shortens optional project context only when the shortened " +
                "request cannot reach another AI provider; this provider route has another provider, so no shortened request was made " +
                "and nothing was saved. " +
                (ex.Primary is null
                    ? "Ask a narrower question."
                    : $"Try again when {ex.Primary.Provider} is available again; it takes the request whole."),
            _ =>
                $"The first Copilot request was sent to {ex.Provider}, but its response failed validation, and the automatic repair attempt " +
                $"(~{ex.EstimatedInputTokens:N0} estimated input tokens) exceeds {ex.Provider}'s configured input budget of " +
                $"{ex.InputBudget:N0} tokens by ~{ex.OverBudgetTokens:N0}. The repair attempt was not sent and nothing was saved. Try again."
        },
        ex.Reason == Application.Knowledge.CopilotRefusalReason.RetryNotSafe && ex.Primary is not null,
        new Dictionary<string, object?>
        {
            ["provider"] = ex.Provider,
            ["sentToProvider"] = false,
            ["stage"] = ex.Stage,
            ["reason"] = ex.Reason.ToString(),
            ["estimatedInputTokens"] = ex.EstimatedInputTokens,
            ["inputBudget"] = ex.InputBudget,
            ["overBudgetTokens"] = ex.OverBudgetTokens
        }.WithPrimary(ex.Primary));

    // When the refusing provider was only the fallback, the detail leads with why the primary did not
    // serve the request - usually the actual blocker (e.g. Gemini's daily quota). The outcome is the
    // primary's sanitized provider message, capped (PrimaryProviderOutcome).
    private static string PrimaryDetail(PrimaryProviderOutcome? primary) => primary is null ? string.Empty
        : $"{primary.Provider}, the primary AI provider, did not serve this request ({primary.Outcome.TrimEnd('.', ' ')}), " +
          "so it went to the fallback provider. ";

    // "Gemini rate limit reached. Please wait and try again." - with the provider's retry hint when
    // it gave one. A used-up daily/zero-limit quota says so instead of promising a short wait.
    private static (int, string, string, string, bool, Dictionary<string, object?>) MapRateLimit(
        AiProviderException ai, Dictionary<string, object?> extra)
    {
        var retryAfterSeconds = ai.RetryAfter is { } wait ? (int?)Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)) : null;
        extra["retryAfterSeconds"] = retryAfterSeconds;
        extra["quotaExhausted"] = ai.QuotaExhausted;

        // The provider call that reported the limit; fallback and part wrappers keep it as InnerException.
        var reported = ai;
        while (reported.RateLimitType is null && reported.InnerException is AiProviderException inner)
        {
            reported = inner;
        }

        extra["rateLimitType"] = reported.RateLimitType;
        var limit = reported.RateLimitType is { } type ? $"{reported.Provider} reported that its {LimitName(type)} limit ({type}) was reached. " : "";

        var detail = limit + (ai.QuotaExhausted
            ? "The AI usage quota appears to be used up for now (or a spend limit was reached). Please wait and try again later, or ask an administrator to check the provider account's limits."
            : retryAfterSeconds is { } seconds
                ? $"Please wait and try again in about {FormatWait(seconds)}."
                : "Please wait and try again.");

        return (StatusCodes.Status429TooManyRequests, "ai_rate_limited", $"{ai.Provider} rate limit reached.", detail, true, extra);
    }

    private static string LimitName(string type) => type switch
    {
        "TPD" => "tokens-per-day",
        "RPD" => "requests-per-day",
        "TPM" => "tokens-per-minute",
        "RPM" => "requests-per-minute",
        _ => type
    };

    private static string FormatWait(int seconds) => seconds switch
    {
        1 => "1 second",
        < 120 => $"{seconds} seconds",
        < 7200 => $"{(int)Math.Ceiling(seconds / 60.0)} minutes",
        _ => $"{(int)Math.Ceiling(seconds / 3600.0)} hours"
    };
}

internal static class PrimaryOutcomeExtras
{
    // Machine-readable form of PrimaryDetail; nothing is added when there was no primary failure.
    public static Dictionary<string, object?> WithPrimary(this Dictionary<string, object?> extra, PrimaryProviderOutcome? primary)
    {
        if (primary is null)
        {
            return extra;
        }

        extra["primaryProvider"] = primary.Provider;
        extra["primaryOutcome"] = primary.Outcome;
        extra["primaryFailureKind"] = primary.Failure?.Kind.ToString();
        extra["primaryQuotaExhausted"] = primary.Failure?.QuotaExhausted;
        extra["primaryRetryAfterSeconds"] = primary.Failure?.RetryAfter is { } wait
            ? (int?)Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))
            : null;
        return extra;
    }
}
