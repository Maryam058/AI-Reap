using AiReap.Application.Ai;
using AiReap.Application.Common;
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
            AiFailureKind.RateLimited => (StatusCodes.Status429TooManyRequests, "ai_rate_limited",
                "The AI provider's rate limit or quota was reached.",
                ai.RetryAfter is { } wait
                    ? $"Too many AI requests right now. Try again in about {Math.Ceiling(wait.TotalSeconds)} seconds."
                    : "Too many AI requests right now (or the daily free-tier quota is used up). Try again later.", true, extra),
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
}
