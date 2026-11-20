import type { AiUsageSnapshot } from '../api/types';

/** Whole tokens with thousands separators, e.g. 12,480. */
export function formatTokens(n: number): string {
  return Math.round(n).toLocaleString('en-US');
}

/** Date and time to the second, e.g. "Nov 6, 2026, 6:41:07 PM": calls minutes apart must be told apart. */
export function formatCallTime(iso: string, locale?: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleString(locale, { month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit', second: '2-digit' });
}

/** True when the provider has reported no call since the API started (a fresh start, or the development stub). */
export function isEmptyUsage(snapshot: AiUsageSnapshot): boolean {
  return snapshot.totals.requests === 0;
}

/** Models with the most tokens (input + output) first. */
export function modelsByTokens(snapshot: AiUsageSnapshot) {
  return [...snapshot.byModel].sort(
    (a, b) => b.totals.inputTokens + b.totals.outputTokens - (a.totals.inputTokens + a.totals.outputTokens) || a.model.localeCompare(b.model),
  );
}
