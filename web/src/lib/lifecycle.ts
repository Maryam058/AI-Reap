import { ROLE_LABELS, TRANSITION_STATE, type StageTransition } from '../api/types.ts';

/**
 * The project lifecycle (§5): eight stages, in order. `Project.status` is the stage the project is
 * in - the only lifecycle state there is. Evidence (exit checks) says whether it may leave that
 * stage; agent activity is separate and never moves it.
 *
 * Owners and gates are shown from the backend's policy (GET /api/lifecycle/policy, and the
 * lifecycle state of a project); the static text below is display copy only.
 */
export interface LifecycleStage {
  status: number;
  label: string;
  description: string;
}

export const LIFECYCLE_STAGES: LifecycleStage[] = [
  { status: 0, label: 'Draft', description: 'Project set up: business problem, objectives, scope and team.' },
  { status: 1, label: 'Requirements Gathering', description: 'Raw requirements, objectives and stakeholders captured.' },
  { status: 2, label: 'Analysis', description: 'Requirements structured, clarified, checked and approved.' },
  { status: 3, label: 'Design', description: 'Architecture, data and APIs designed and reviewed by a developer.' },
  { status: 4, label: 'Approved', description: 'Baseline approved; implementation is planned.' },
  { status: 5, label: 'Implementation', description: 'Planned tasks completed by developers.' },
  { status: 6, label: 'Testing', description: 'Test cases run and passed; QA signs off.' },
  { status: 7, label: 'Completed', description: 'Delivery approved by a reviewer.' },
];

export const LAST_STAGE = LIFECYCLE_STAGES.length - 1;

export type StageState = 'completed' | 'current' | 'upcoming';

export function stageLabel(status: number | null | undefined): string {
  return status == null ? '—' : (LIFECYCLE_STAGES[status]?.label ?? `Stage ${status}`);
}

export function stageState(stage: number, projectStatus: number): StageState {
  return stage < projectStatus ? 'completed' : stage === projectStatus ? 'current' : 'upcoming';
}

export const STAGE_STATE_LABEL: Record<StageState, string> = {
  completed: 'Completed',
  current: 'Current stage',
  upcoming: 'Upcoming',
};

/**
 * Progress across the lifecycle, from Project.status alone: Draft is 0%, Completed 100%. The
 * current stage's exit checks are shown alongside, never blended into this number.
 */
export function lifecycleProgress(status: number): { stage: number; stageNumber: number; percent: number; finished: boolean } {
  const stage = Math.min(Math.max(status, 0), LAST_STAGE);
  return { stage, stageNumber: stage + 1, percent: Math.round((stage / LAST_STAGE) * 100), finished: stage === LAST_STAGE };
}

export function roleLabel(role: string | null | undefined): string {
  return role ? (ROLE_LABELS[role] ?? role) : '—';
}

/** Leaving these stages needs a Reviewer's decision (mirrors the backend policy, for display). */
export function isGatedStage(status: number): boolean {
  return status === 3 || status === 6;
}

/** The most recent rejected request out of the current stage, if nothing newer superseded it. */
export function latestRejection(history: StageTransition[], status: number): StageTransition | null {
  // Newest first: stop at the move that brought the project into this stage, or at a newer request.
  for (let i = history.length - 1; i >= 0; i--) {
    const t = history[i];
    if (t.toStatus === status && t.state === TRANSITION_STATE.Completed) return null;
    if (t.fromStatus === status && t.state === TRANSITION_STATE.Rejected) return t;
    if (t.fromStatus === status && t.state === TRANSITION_STATE.Pending) return null;
  }
  return null;
}
