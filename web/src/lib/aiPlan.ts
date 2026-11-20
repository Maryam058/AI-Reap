// Imported with its extension so `node --test` can load this module directly (tests/aiPlan.test.ts).
import type { AiPlanProvider, AiPlanResult, AiPlanStatusValue } from '../api/types.ts';

/**
 * Wording for the dry-run AI plan shown next to Run AI analysis / Generate FR / NFR. Every figure
 * comes from the backend (GET .../ai-plan), which plans with the same splitting code a real run
 * uses and contacts no AI provider. Nothing here decides anything: the buttons stay enabled and a
 * run is still refused (saving nothing) by the backend if a limit applies. A configured provider is
 * never described as available - its quota is unknown until a request is sent.
 */

export type PlanTone = 'ok' | 'warn' | 'block';

export function planTone(status: AiPlanStatusValue): PlanTone {
  if (status === 'WithinLimits') return 'ok';
  if (status === 'Warning') return 'warn';
  return 'block';
}

export function statusLabel(status: AiPlanStatusValue): string {
  switch (status) {
    case 'WithinLimits':
      return 'Within size limits';
    case 'Warning':
      return 'Warning';
    case 'Uncertain':
      return 'Uncertain';
    default:
      return 'Blocked';
  }
}

/** Whether the plan should be called out before the user runs the operation. */
export function planNeedsAttention(plan: AiPlanResult): boolean {
  return plan.overall.status !== 'WithinLimits';
}

/** 85895 -> "~86k", 940 -> "~940". Estimates are rounded: they are not exact token counts. */
export function approxTokens(tokens: number): string {
  if (tokens >= 1000) return `~${Math.round(tokens / 1000)}k`;
  return `~${tokens}`;
}

function clock(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

/** "one whole request (~86k tokens)", "68 parts", "66–68 parts", or "cannot be split". */
export function partsText(p: AiPlanProvider): string {
  if (p.wholeRequest) return `one whole request (${approxTokens(p.estimatedRequestTokens)} tokens)`;
  if (!p.parts) return 'cannot be split';
  const count = p.parts.exact ? `${p.parts.max}` : `${p.parts.min}–${p.parts.max}`;
  return `${count} part${p.parts.max === 1 ? '' : 's'}`;
}

export function availabilityText(p: AiPlanProvider): string {
  switch (p.availability) {
    case 'CoolingDown':
      return p.cooldownUntil ? `skipped until ${clock(p.cooldownUntil)}` : 'skipped (cool-down)';
    case 'Stub':
      return 'development stub';
    case 'NotConfigured':
      return 'not configured';
    default:
      return 'quota unknown';
  }
}

/** One line per provider, e.g. "Groq (fallback): 68 parts, limit 8 · quota unknown · Blocked". */
export function providerLine(p: AiPlanProvider): string {
  const limit = !p.wholeRequest && p.maxParts > 0 ? `, limit ${p.maxParts}` : '';
  return `${p.provider} (${p.role.toLowerCase()}): ${partsText(p)}${limit} · ${availabilityText(p)} · ${statusLabel(p.status)}`;
}

/** Title text for a button whose plan needs attention; undefined when it does not. */
export function planButtonHint(plan: AiPlanResult | null): string | undefined {
  if (!plan || !planNeedsAttention(plan)) return undefined;
  const lead =
    plan.overall.status === 'Blocked'
      ? 'AI plan: this run is expected to be refused before anything is sent or saved.'
      : plan.overall.status === 'Uncertain'
        ? 'AI plan: this run may be refused before anything is sent or saved.'
        : 'AI plan: this run may fail.';
  return `${lead} ${plan.overall.summary} (Estimate; no AI provider was contacted.)`;
}

/** Blocking and warning reasons across providers, without repeats - for the plan's detail view. */
export function attentionReasons(plan: AiPlanResult): { provider: string; severity: string; message: string }[] {
  const seen = new Set<string>();
  const out: { provider: string; severity: string; message: string }[] = [];
  for (const p of plan.providers) {
    for (const r of p.reasons) {
      if (r.severity === 'Info') continue;
      const key = `${p.provider}|${r.code}`;
      if (seen.has(key)) continue;
      seen.add(key);
      out.push({ provider: p.provider, severity: r.severity, message: r.message });
    }
  }
  return out;
}
