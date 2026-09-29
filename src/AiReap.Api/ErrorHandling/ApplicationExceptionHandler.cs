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
            AiProviderException ai => MapProvider(ai),

            AiOutputValidationException invalid => (
                StatusCodes.Status502BadGateway, "ai_invalid_output",
                "The AI response could not be used.",
                invalid.Repairable
                    ? "The AI returned output that failed validation, even after one automatic correction attempt. Nothing was saved. Try again; if it keeps failing, simplify or shorten the requirement text."
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
                "The AI request timed out. Try again; very long requirement text may need to be split.", true, extra),
            AiFailureKind.Unavailable => (StatusCodes.Status503ServiceUnavailable, "ai_unavailable",
                "The AI provider is temporarily unavailable.", "The AI service could not be reached. Try again shortly.", true, extra),
            AiFailureKind.ContentBlocked => (StatusCodes.Status422UnprocessableEntity, "ai_content_blocked",
                "The AI provider refused to process this content.", ai.Message, false, extra),
            _ => (StatusCodes.Status502BadGateway, "ai_request_rejected",
                "The AI provider rejected the request.", ai.ProviderMessage ?? ai.Message, false, extra)
        };
    }

    // "Gemini rate limit reached. Please wait and try again." - with the provider's retry hint when
    // it gave one. A used-up daily/zero-limit quota says so instead of promising a short wait.
    private static (int, string, string, string, bool, Dictionary<string, object?>) MapRateLimit(
        AiProviderException ai, Dictionary<string, object?> extra)
    {
        var retryAfterSeconds = ai.RetryAfter is { } wait ? (int?)Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)) : null;
        extra["retryAfterSeconds"] = retryAfterSeconds;
        extra["quotaExhausted"] = ai.QuotaExhausted;

        var detail = ai.QuotaExhausted
            ? "The AI usage quota appears to be used up for now (for example a daily free-tier limit). Please wait and try again later."
            : retryAfterSeconds is { } seconds
                ? $"Please wait and try again in about {FormatWait(seconds)}."
                : "Please wait and try again.";

        return (StatusCodes.Status429TooManyRequests, "ai_rate_limited", $"{ai.Provider} rate limit reached.", detail, true, extra);
    }

    private static string FormatWait(int seconds) => seconds switch
    {
        1 => "1 second",
        < 120 => $"{seconds} seconds",
        < 7200 => $"{(int)Math.Ceiling(seconds / 60.0)} minutes",
        _ => $"{(int)Math.Ceiling(seconds / 3600.0)} hours"
    };
}
