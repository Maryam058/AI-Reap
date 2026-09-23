import { ApiError } from '../api/client';

/**
 * Turns an API failure into a short, user-facing sentence. The API sometimes replies with a JSON
 * string / array / { message } payload; anything else falls back to the caller's wording so raw
 * server output (or a stack trace) is never shown.
 */
export function apiErrorText(err: unknown, fallback: string): string {
  if (!(err instanceof ApiError)) return fallback;
  if (err.status === 403) return 'You do not have permission to do that.';
  if (err.status === 404) return 'That item no longer exists.';
  if (err.status >= 500 || err.status === 0) return fallback;
  try {
    const parsed = JSON.parse(err.message) as unknown;
    if (typeof parsed === 'string') return parsed;
    if (Array.isArray(parsed) && parsed.every((p) => typeof p === 'string')) return parsed.join(' ');
    if (parsed && typeof parsed === 'object') {
      const m = (parsed as { message?: unknown; title?: unknown }).message ?? (parsed as { title?: unknown }).title;
      if (typeof m === 'string') return m;
    }
  } catch {
    // plain-text body
  }
  return err.message && err.message.length < 200 && !err.message.includes('\n') ? err.message : fallback;
}
