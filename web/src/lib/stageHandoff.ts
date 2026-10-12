// Imported with their extensions so `node --test` can load this module directly (tests/stageHandoff.test.ts).
import { TRANSITION_KIND, TRANSITION_STATE, type ProjectLifecycleState, type ProjectMember, type StageTransition } from '../api/types.ts';
import { stageLabel } from './lifecycle.ts';
import { roleGuard, roleName } from './roleGuard.ts';

/**
 * Who picks the project up after a stage is completed - the "hand-off" shown once a lifecycle move
 * has really happened. Everything comes from the backend: the transition the API returned (it must
 * be a completed Advance) and the lifecycle state fetched again afterwards (the new stage, its
 * owner role from ProjectLifecyclePolicy, and what the caller may now do). Nothing here moves the
 * project, logs anyone out or grants anything; it only explains who acts next.
 */
export type HandoffAccess =
  /** The caller holds the new stage's owner role (and the backend lets them move it): carry on. */
  | 'continue'
  /** The caller is an Administrator without that role: they may act only as an override, with a reason. */
  | 'administrator'
  /** The caller lacks the role: a different user, with the role and project access, has to sign in. */
  | 'switch-user'
  /** The lifecycle is finished; nobody else needs to sign in. */
  | 'complete';

export interface StageHandoff {
  transitionId: string;
  completedStage: string;
  /** The stage the project is now in (Completed when the lifecycle is finished). */
  nextStage: string;
  /** The role that owns, and moves on, the new stage; null once the lifecycle is complete. */
  nextRole: string | null;
  access: HandoffAccess;
  title: string;
  message: string;
  instruction: string;
  /** Set when leaving the new stage needs a Reviewer's approval. */
  gateNote: string | null;
}

/**
 * The hand-off for `transition`, judged against `state` - the lifecycle state re-fetched after it.
 * Null when there is nothing to hand off: a pending request, a rejection or withdrawal, a rollback,
 * or a state that no longer matches the move (someone else has moved the project since).
 */
export function stageHandoff(transition: StageTransition, state: ProjectLifecycleState, userRoles: readonly string[]): StageHandoff | null {
  if (transition.kind !== TRANSITION_KIND.Advance || transition.state !== TRANSITION_STATE.Completed) return null;
  if (transition.projectId !== state.projectId || transition.toStatus !== state.status) return null;

  const completedStage = stageLabel(transition.fromStatus);
  const nextStage = state.stageLabel;
  const base = { transitionId: transition.id, completedStage, nextStage };

  if (state.nextStatus == null) {
    return {
      ...base,
      nextRole: null,
      access: 'complete',
      title: 'Project lifecycle complete',
      message: `${completedStage} is approved and the project is now ${nextStage}. Every SDLC stage is done.`,
      instruction: 'No further sign-in is needed.',
      gateNote: null,
    };
  }

  const role = state.ownerRole;
  const name = roleName(role);
  const { actions } = state;
  // The backend's own answer for this caller, project membership included; the role alone is not enough.
  const mayMove = (actions.canAdvance || actions.canSubmitForApproval) && !actions.advanceIsOverride;
  const access: HandoffAccess = mayMove ? 'continue' : roleGuard(role, userRoles).kind === 'administrator' ? 'administrator' : 'switch-user';
  const current = userRoles.length > 0 ? `You are signed in as ${userRoles.map(roleName).join(', ')}.` : 'You are signed in without a project role.';

  return {
    ...base,
    nextRole: role,
    access,
    title: `${completedStage} stage complete`,
    message:
      access === 'continue'
        ? `You hold the ${name} role, so you can continue to the ${nextStage} stage without signing in again.`
        : `Please log in with the ${name} role to continue to the ${nextStage} stage.`,
    instruction:
      access === 'continue'
        ? `${nextStage} is now owned by the ${name} role.`
        : access === 'administrator'
          ? `${current} An Administrator can act on ${nextStage} only as an override, with a reason. Normally, sign out and let a ${name} who is a member of this project sign in to continue.`
          : `${current} Sign out, and a ${name} who is a member of this project should sign in to continue.`,
    gateNote: state.nextIsGated
      ? `When ${nextStage} is done, the ${name} submits it for approval; a Reviewer other than the submitter approves or rejects it.`
      : null,
  };
}

/**
 * Remembers which transitions have already been handed off, so one move shows its popup once -
 * however often the panel re-renders or the lifecycle is re-fetched.
 */
export function createHandoffTracker() {
  const shown = new Set<string>();
  return {
    /** True the first time a transition id is claimed, false afterwards. */
    claim(transitionId: string): boolean {
      if (shown.has(transitionId)) return false;
      shown.add(transitionId);
      return true;
    },
  };
}

/**
 * The members who can pick up a stage owned by `role`: project members (GET .../members - the
 * stakeholder list is separate and grants no access) whose account holds that role and is active.
 * An Administrator's override is not membership in the role, so their account never counts here.
 */
export function eligibleMembers(role: string, members: readonly ProjectMember[]): ProjectMember[] {
  return members.filter((m) => m.isActive && m.role === role);
}

/** What to show after a completed move, once the next role's project membership has been checked. */
export type HandoffPopup =
  /** The usual hand-off: the lifecycle is complete, or someone holding the next role can pick it up. */
  | { kind: 'handoff'; handoff: StageHandoff }
  /** Nobody on the project holds the next role: grant project access before going on. */
  | { kind: 'missing-member'; handoff: StageHandoff }
  /** The member list could not be loaded; we don't know either way. */
  | { kind: 'membership-error'; handoff: StageHandoff };

/**
 * Checks, against the project's member list as `loadMembers` fetches it now, that someone can pick
 * up the next stage. The final stage asks for nobody, so it is never checked. A failed fetch is
 * reported as such, never as a missing member.
 */
export async function resolveHandoffPopup(handoff: StageHandoff, loadMembers: () => Promise<readonly ProjectMember[]>): Promise<HandoffPopup> {
  if (handoff.access === 'complete' || handoff.nextRole == null) return { kind: 'handoff', handoff };
  let members: readonly ProjectMember[];
  try {
    members = await loadMembers();
  } catch {
    return { kind: 'membership-error', handoff };
  }
  return eligibleMembers(handoff.nextRole, members).length > 0 ? { kind: 'handoff', handoff } : { kind: 'missing-member', handoff };
}

/** The text of the missing-member popup. */
export function missingMemberMessage(role: string): string {
  const name = roleName(role);
  return `No project member with the required ${name} role is assigned to this project. Please grant project access to an eligible ${name} user before proceeding.`;
}

/**
 * Moves whose next role was missing (or couldn't be checked) when the user went off to manage the
 * project's members. When the lifecycle panel is shown again, the move is re-judged against freshly
 * fetched lifecycle state and members; nothing is advanced on the user's behalf.
 */
export function createMembershipFollowUps() {
  const pending = new Map<string, StageTransition>();
  return {
    remember(transition: StageTransition) {
      pending.set(transition.projectId, transition);
    },
    /** The remembered move for a project, if any. */
    peek(projectId: string): StageTransition | null {
      return pending.get(projectId) ?? null;
    },
    /** Done with it: it has been rechecked (or dismissed), so it isn't rechecked again. */
    forget(projectId: string) {
      pending.delete(projectId);
    },
  };
}
