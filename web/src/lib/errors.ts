import { ApiError } from '../api/client';

// Stable codes the API puts on ProblemDetails responses (ApplicationExceptionHandler). Their
// `detail` text is written for end users and never contains secrets or stack traces.
const USER_FACING_CODES = new Set([
  'ai_not_configured',
  'ai_auth_failed',
  'ai_rate_limited',
  'ai_timeout',
  'ai_unavailable',
  'ai_request_rejected',
  'ai_content_blocked',
  'ai_invalid_output',
  'invalid_status_transition',
  'validation_failed',
]);

interface ProblemDetails {
  title?: unknown;
  detail?: unknown;
  code?: unknown;
  retryable?: unknown;
  message?: unknown;
}

function parseBody(err: ApiError): unknown {
  try {
    return JSON.parse(err.message) as unknown;
  } catch {
    return undefined; // plain-text body
  }
}

/** The machine-readable error code from an API ProblemDetails response, if any. */
export function apiErrorCode(err: unknown): string | null {
  if (!(err instanceof ApiError)) return null;
  const parsed = parseBody(err) as ProblemDetails | undefined;
  return parsed && typeof parsed === 'object' && typeof parsed.code === 'string' ? parsed.code : null;
}

/** True when the API says trying the same request again later may succeed (rate limit, timeout, outage). */
export function isRetryableApiError(err: unknown): boolean {
  if (!(err instanceof ApiError)) return false;
  const parsed = parseBody(err) as ProblemDetails | undefined;
  return !!parsed && typeof parsed === 'object' && parsed.retryable === true;
}

/**
 * Turns an API failure into a short, user-facing sentence. Known application errors (AI provider
 * failures, rejected status changes, validation) show the API's own explanation - including for
 * 5xx AI errors, which previously all read "An unexpected error occurred". Anything else falls back
 * to the caller's wording so raw server output (or a stack trace) is never shown.
 */
export function apiErrorText(err: unknown, fallback: string): string {
  if (!(err instanceof ApiError)) return fallback;

  const parsed = parseBody(err);
  if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
    const problem = parsed as ProblemDetails;
    if (typeof problem.code === 'string' && USER_FACING_CODES.has(problem.code)) {
      const title = typeof problem.title === 'string' ? problem.title : '';
      const detail = typeof problem.detail === 'string' ? problem.detail : '';
      return [title, detail].filter(Boolean).join(' ') || fallback;
    }
  }

  if (err.status === 403) return 'You do not have permission to do that.';
  if (err.status === 404) return 'That item no longer exists.';
  if (err.status >= 500 || err.status === 0) return fallback;

  if (typeof parsed === 'string') return parsed;
  if (Array.isArray(parsed) && parsed.every((p) => typeof p === 'string')) return parsed.join(' ');
  if (parsed && typeof parsed === 'object') {
    const problem = parsed as ProblemDetails;
    const m = problem.message ?? problem.detail ?? problem.title;
    if (typeof m === 'string') return m;
  }
  return err.message && err.message.length < 200 && !err.message.includes('\n') ? err.message : fallback;
}
