import { lifecycleProgress } from './lifecycle.ts';

/**
 * Dashboard-only health summary of a project, derived from signals the dashboard already loads
 * (lifecycle state and project dashboard counts). It is display copy: it never gates or moves a
 * project, and a Completed project always reads as Completed.
 */
export type ProjectHealth = 'completed' | 'attention' | 'on-track' | 'unknown';

export interface HealthSignals {
  /** A stage-transition request is waiting for a Reviewer. */
  pendingReview: boolean;
  /** Stages already passed whose exit checks no longer hold. */
  evidenceGaps: number;
  conflicts: number | null;
  /** Downstream artifacts flagged by an approved upstream change nobody has reviewed yet. */
  impactNotices: number | null;
}

export const HEALTH_LABEL: Record<ProjectHealth, string> = {
  completed: 'Completed',
  attention: 'Needs attention',
  'on-track': 'On track',
  unknown: 'Not assessed',
};

export const HEALTH_ORDER: ProjectHealth[] = ['attention', 'on-track', 'completed', 'unknown'];

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/** `signals` is null when the project's details were not loaded (outside the sample, or a failed request). */
export function projectHealth(status: number, signals: HealthSignals | null): { health: ProjectHealth; reasons: string[] } {
  if (lifecycleProgress(status).finished) return { health: 'completed', reasons: [] };
  if (!signals) return { health: 'unknown', reasons: [] };
  const reasons: string[] = [];
  if (signals.pendingReview) reasons.push('Awaiting Reviewer approval');
  if (signals.evidenceGaps > 0) reasons.push(plural(signals.evidenceGaps, 'evidence gap', 'evidence gaps'));
  if ((signals.conflicts ?? 0) > 0) reasons.push(plural(signals.conflicts!, 'conflict', 'conflicts'));
  if ((signals.impactNotices ?? 0) > 0) reasons.push(plural(signals.impactNotices!, 'change to review', 'changes to review'));
  return { health: reasons.length > 0 ? 'attention' : 'on-track', reasons };
}
