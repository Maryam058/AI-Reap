// Run with `npm test`. Covers the check, after a completed lifecycle move, that the project has a
// member who can pick up the next stage. Membership and roles are the backend's (GET
// /api/projects/{id}/members, ProjectsController.ToMemberResponseAsync); the fixtures stand in for it.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { TRANSITION_KIND, TRANSITION_STATE, type LifecycleActions, type ProjectLifecycleState, type ProjectMember, type StageTransition } from '../src/api/types.ts';
import { createMembershipFollowUps, eligibleMembers, missingMemberMessage, resolveHandoffPopup, stageHandoff, type StageHandoff } from '../src/lib/stageHandoff.ts';

const PROJECT = 'p1';

// Mirrors ProjectLifecyclePolicy.Stages (label, owner role).
const POLICY: [string, string][] = [
  ['Draft', 'BusinessAnalyst'],
  ['Requirements Gathering', 'BusinessAnalyst'],
  ['Analysis', 'BusinessAnalyst'],
  ['Design', 'Developer'],
  ['Approved', 'Reviewer'],
  ['Implementation', 'Developer'],
  ['Testing', 'QA'],
  ['Completed', 'Reviewer'],
];

const NO_ACTIONS: LifecycleActions = { canAdvance: false, canSubmitForApproval: false, canDecide: false, canWithdraw: false, canRollback: false, canOverride: false };

function moved(from: number): StageTransition {
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
  };
}

function stateAt(status: number, actions: Partial<LifecycleActions> = {}): ProjectLifecycleState {
  const [label, owner] = POLICY[status];
  return {
    projectId: PROJECT,
    status,
    stageLabel: label,
    ownerRole: owner,
    nextStatus: status === POLICY.length - 1 ? null : status + 1,
    nextIsGated: status === 3 || status === 6,
    requiresSignOff: status === 6,
    exitChecks: [],
    readyToAdvance: false,
    pendingTransition: null,
    evidenceGaps: [],
    actions: { ...NO_ACTIONS, ...actions },
    history: [],
  };
}

/** A row of GET .../members: the account's role, and whether it is active. */
function member(userId: string, role: string | null, isActive = true): ProjectMember {
  return { id: `m-${userId}`, projectId: PROJECT, userId, email: `${userId}@example.com`, displayName: userId, addedByUserId: 'ba', addedAt: '2026-10-01T00:00:00Z', role, isActive };
}

/** Analysis -> Design, completed by a Business Analyst: the Developer picks it up. */
const toDesign = (): StageHandoff => stageHandoff(moved(2), stateAt(3), ['BusinessAnalyst'])!;

/** A member loader that records how often it was called. */
function loader(members: ProjectMember[] | Error) {
  const calls = { count: 0 };
  const load = async () => {
    calls.count++;
    if (members instanceof Error) throw members;
    return members;
  };
  return { load, calls };
}

test('a project member holding the next role: the usual hand-off is shown', async () => {
  const { load, calls } = loader([member('ba', 'BusinessAnalyst'), member('dev', 'Developer')]);
  const popup = await resolveHandoffPopup(toDesign(), load);
  assert.equal(popup.kind, 'handoff');
  assert.equal(popup.handoff.nextRole, 'Developer');
  assert.equal(calls.count, 1);
});

test('no project member holds the next role: the missing-member popup is shown', async () => {
  const popup = await resolveHandoffPopup(toDesign(), loader([member('ba', 'BusinessAnalyst'), member('qa', 'QA')]).load);
  assert.equal(popup.kind, 'missing-member');
  assert.equal(popup.handoff.completedStage, 'Analysis');
  assert.equal(popup.handoff.nextStage, 'Design');
  assert.equal(
    missingMemberMessage(popup.handoff.nextRole!),
    'No project member with the required Developer role is assigned to this project. Please grant project access to an eligible Developer user before proceeding.',
  );
});

test('a stakeholder-only user does not count: stakeholders are not project members', async () => {
  // Recorded as a stakeholder (ProjectStakeholder, with a Developer account) but never added as a
  // member, so the members endpoint doesn't return them and they can't open the project.
  const stakeholderOnlyDeveloper = 'dev-stakeholder';
  const members = [member('ba', 'BusinessAnalyst')];
  assert.ok(!members.some((m) => m.userId === stakeholderOnlyDeveloper));
  assert.equal((await resolveHandoffPopup(toDesign(), loader(members).load)).kind, 'missing-member');
});

test("an Administrator's override access does not count as the required role", async () => {
  // An Administrator member, and an Administrator acting on Design as an override.
  const handoff = stageHandoff(moved(2), stateAt(3, { canSubmitForApproval: true, advanceIsOverride: true, canOverride: true }), ['Administrator'])!;
  assert.equal(handoff.access, 'administrator');
  const popup = await resolveHandoffPopup(handoff, loader([member('admin', 'Administrator'), member('ba', 'BusinessAnalyst')]).load);
  assert.equal(popup.kind, 'missing-member');
});

test('deactivated or deleted accounts do not count', () => {
  assert.deepEqual(eligibleMembers('Developer', [member('dev', 'Developer', false), member('gone', null, false)]), []);
  assert.deepEqual(
    eligibleMembers('Developer', [member('dev', 'Developer', false), member('dev2', 'Developer')]).map((m) => m.userId),
    ['dev2'],
  );
});

test('after a member is added, the recheck uses fresh data and shows the hand-off without moving the project', async () => {
  const members = [member('ba', 'BusinessAnalyst')];
  const load = async () => [...members];
  const followUps = createMembershipFollowUps();

  const first = await resolveHandoffPopup(toDesign(), load);
  assert.equal(first.kind, 'missing-member');
  followUps.remember(moved(2)); // the user goes off to manage members

  members.push(member('dev', 'Developer')); // a Business Analyst adds a Developer

  // Back on the overview: the remembered move is re-judged against freshly fetched state.
  const remembered = followUps.peek(PROJECT)!;
  assert.equal(remembered.id, 't-2');
  const fresh = stageHandoff(remembered, stateAt(3), ['BusinessAnalyst'])!;
  const second = await resolveHandoffPopup(fresh, load);
  followUps.forget(PROJECT);

  assert.equal(second.kind, 'handoff');
  assert.equal(second.handoff.nextStage, 'Design'); // still Design: nothing advanced it
  assert.equal(followUps.peek(PROJECT), null); // rechecked once only
});

test('a remembered move is dropped when the project has moved on since', () => {
  const followUps = createMembershipFollowUps();
  followUps.remember(moved(2));
  // Someone else moved Design on before the user came back.
  assert.equal(stageHandoff(followUps.peek(PROJECT)!, stateAt(4), ['BusinessAnalyst']), null);
});

test('a member-list failure is reported as an error, never as a missing member', async () => {
  const popup = await resolveHandoffPopup(toDesign(), loader(new Error('503 Service Unavailable')).load);
  assert.equal(popup.kind, 'membership-error');
  assert.equal(popup.handoff.nextRole, 'Developer');
});

test('the final Completed stage keeps the success popup and skips the membership check', async () => {
  const handoff = stageHandoff(moved(6), stateAt(7), ['Reviewer'])!;
  const { load, calls } = loader(new Error('should not be called'));
  const popup = await resolveHandoffPopup(handoff, load);
  assert.equal(popup.kind, 'handoff');
  assert.equal(popup.handoff.access, 'complete');
  assert.equal(popup.handoff.title, 'Project lifecycle complete');
  assert.equal(calls.count, 0);
});
