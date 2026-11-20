// Run with `npm test`. Covers the hand-off shown after a completed lifecycle move. The rules
// themselves (ownership, gates, separation of duties, evidence, membership) are the backend's and
// are tested in AiReap.Tests; the state fixtures below stand in for its answers.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { TRANSITION_KIND, TRANSITION_STATE, type LifecycleActions, type ProjectLifecycleState, type StageTransition } from '../src/api/types.ts';
import { LIFECYCLE_STAGES } from '../src/lib/lifecycle.ts';
import { createHandoffTracker, stageHandoff } from '../src/lib/stageHandoff.ts';

const PROJECT = 'p1';

// Mirrors ProjectLifecyclePolicy.Stages (status, label, owner role) and its gates.
const POLICY: [number, string, string][] = [
  [0, 'Draft', 'BusinessAnalyst'],
  [1, 'Requirements Gathering', 'BusinessAnalyst'],
  [2, 'Analysis', 'BusinessAnalyst'],
  [3, 'Design', 'Developer'],
  [4, 'Approved', 'Reviewer'],
  [5, 'Implementation', 'Developer'],
  [6, 'Testing', 'QA'],
  [7, 'Completed', 'Reviewer'],
];
const GATED = new Set([3, 6]);

const NO_ACTIONS: LifecycleActions = { canAdvance: false, canSubmitForApproval: false, canDecide: false, canWithdraw: false, canRollback: false, canOverride: false };

function moved(from: number, overrides: Partial<StageTransition> = {}): StageTransition {
  return {
    id: `t-${from}`,
    projectId: PROJECT,
    sequence: from + 1,
    kind: TRANSITION_KIND.Advance,
    state: TRANSITION_STATE.Completed,
    fromStatus: from,
    toStatus: from + 1,
    requestedByUserId: 'u1',
    requestedAt: '2026-10-09T10:00:00Z',
    isOverride: false,
    evidence: [],
    reviews: [],
    ...overrides,
  };
}

/** The lifecycle state the backend returns for `status`, for a caller who may (or may not) move it. */
function stateAt(status: number, actions: Partial<LifecycleActions> = {}): ProjectLifecycleState {
  const [, label, owner] = POLICY[status];
  const last = status === POLICY.length - 1;
  return {
    projectId: PROJECT,
    status,
    stageLabel: label,
    ownerRole: owner,
    nextStatus: last ? null : status + 1,
    nextIsGated: GATED.has(status),
    requiresSignOff: status === 6,
    exitChecks: [],
    readyToAdvance: false,
    pendingTransition: null,
    evidenceGaps: [],
    actions: { ...NO_ACTIONS, ...actions },
    history: [],
  };
}

/** What the backend grants a member holding the owner role of `status` (gated stages submit). */
const ownerActions = (status: number): Partial<LifecycleActions> => (GATED.has(status) ? { canSubmitForApproval: true } : { canAdvance: true });

test('the client stage sequence matches the backend policy', () => {
  assert.deepEqual(
    LIFECYCLE_STAGES.map((s) => [s.status, s.label]),
    POLICY.map(([status, label]) => [status, label]),
  );
});

test('each completed move names the next stage and its owner role', () => {
  const expected: [number, string, string][] = [
    [0, 'Requirements Gathering', 'Business Analyst'],
    [1, 'Analysis', 'Business Analyst'],
    [2, 'Design', 'Developer'],
    [3, 'Approved', 'Reviewer'],
    [4, 'Implementation', 'Developer'],
    [5, 'Testing', 'QA'],
  ];
  for (const [from, stage, role] of expected) {
    const h = stageHandoff(moved(from), stateAt(from + 1), []);
    assert.ok(h, `hand-off after leaving stage ${from}`);
    assert.equal(h.completedStage, POLICY[from][1]);
    assert.equal(h.nextStage, stage);
    assert.equal(h.nextRole, POLICY[from + 1][2]);
    assert.equal(h.message, `Please log in with the ${role} role to continue to the ${stage} stage.`);
  }
});

test('Analysis -> Design asks a Business Analyst to hand over to a Developer', () => {
  const h = stageHandoff(moved(2), stateAt(3), ['BusinessAnalyst'])!;
  assert.equal(h.access, 'switch-user');
  assert.equal(h.title, 'Analysis stage complete');
  assert.equal(h.message, 'Please log in with the Developer role to continue to the Design stage.');
  assert.match(h.instruction, /^You are signed in as Business Analyst\. Sign out, and a Developer who is a member of this project should sign in to continue\.$/);
});

test('a user who already holds the next role and may move it continues without signing in again', () => {
  // Draft -> Requirements Gathering: the Business Analyst owns both stages.
  const h = stageHandoff(moved(0), stateAt(1, ownerActions(1)), ['BusinessAnalyst'])!;
  assert.equal(h.access, 'continue');
  assert.doesNotMatch(h.message, /log in/i);
  // Design -> Approved, approved by a Reviewer, who owns Approved.
  assert.equal(stageHandoff(moved(3), stateAt(4, ownerActions(4)), ['Reviewer'])!.access, 'continue');
});

test('a role label alone is not enough: the backend must allow the move', () => {
  // Holds Developer, but the backend grants no action (e.g. not able to act on this project).
  const h = stageHandoff(moved(2), stateAt(3), ['Developer'])!;
  assert.notEqual(h.access, 'continue');
  assert.match(h.message, /Please log in with the Developer role/);
});

test('an Administrator is told they act only as an override, never treated as the owner', () => {
  const h = stageHandoff(moved(2), stateAt(3, { canSubmitForApproval: true, advanceIsOverride: true, canOverride: true }), ['Administrator'])!;
  assert.equal(h.access, 'administrator');
  assert.match(h.message, /Please log in with the Developer role to continue to the Design stage\./);
  assert.match(h.instruction, /only as an override, with a reason/);
});

test('gated stages say a Reviewer other than the submitter decides', () => {
  assert.match(stageHandoff(moved(2), stateAt(3), [])!.gateNote ?? '', /Reviewer other than the submitter/);
  assert.match(stageHandoff(moved(5), stateAt(6), [])!.gateNote ?? '', /QA submits it for approval/);
  assert.equal(stageHandoff(moved(4), stateAt(5), [])!.gateNote, null);
});

test('Testing -> Completed is a final success and asks nobody to log in', () => {
  const h = stageHandoff(moved(6), stateAt(7), ['Reviewer'])!;
  assert.equal(h.access, 'complete');
  assert.equal(h.nextRole, null);
  assert.equal(h.title, 'Project lifecycle complete');
  assert.equal(h.instruction, 'No further sign-in is needed.');
  assert.doesNotMatch(`${h.message} ${h.instruction}`, /log in|sign out/i);
});

test('no hand-off for a pending request, a rejection, a withdrawal or a rollback', () => {
  assert.equal(stageHandoff(moved(3, { state: TRANSITION_STATE.Pending }), stateAt(3), []), null);
  assert.equal(stageHandoff(moved(3, { state: TRANSITION_STATE.Rejected }), stateAt(3), []), null);
  assert.equal(stageHandoff(moved(6, { state: TRANSITION_STATE.Withdrawn }), stateAt(6), []), null);
  assert.equal(stageHandoff(moved(4, { kind: TRANSITION_KIND.Rollback, fromStatus: 5, toStatus: 4 }), stateAt(4), []), null);
  assert.equal(stageHandoff(moved(0, { kind: TRANSITION_KIND.Baseline, fromStatus: null, toStatus: 0 }), stateAt(0), []), null);
});

test('no hand-off when the refreshed state does not match the move', () => {
  // Someone moved the project on again before the state was fetched.
  assert.equal(stageHandoff(moved(2), stateAt(4), []), null);
  // Not the same project.
  assert.equal(stageHandoff(moved(2), { ...stateAt(3), projectId: 'other' }, []), null);
});

test('one transition shows its hand-off once', () => {
  const tracker = createHandoffTracker();
  assert.equal(tracker.claim('t-2'), true);
  assert.equal(tracker.claim('t-2'), false);
  assert.equal(tracker.claim('t-2'), false);
  assert.equal(tracker.claim('t-3'), true);
});
