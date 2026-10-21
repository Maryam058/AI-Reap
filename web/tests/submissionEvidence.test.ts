// Run with `npm test`. Covers the D3 notice shown when the backend reports a stage transition's
// submission-time evidence unavailable. Whether a row is flagged is the backend's decision
// (ProjectLifecycleService.SubmissionEvidenceUnavailable, tested in AiReap.Tests); this only checks
// that the flag alone drives the notice.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { TRANSITION_KIND, TRANSITION_STATE, type StageTransition } from '../src/api/types.ts';
import { SUBMISSION_EVIDENCE_UNAVAILABLE_NOTE, submissionEvidenceNote } from '../src/lib/lifecycle.ts';

// An approved gated request (Design -> Approved), as GET /lifecycle/history returns it.
function approvedGate(overrides: Partial<StageTransition> = {}): StageTransition {
  return {
    id: 't-1',
    projectId: 'p1',
    sequence: 4,
    kind: TRANSITION_KIND.Advance,
    state: TRANSITION_STATE.Completed,
    fromStatus: 3,
    toStatus: 4,
    requestedByUserId: 'dev',
    requestedByRole: 'Developer',
    requestedAt: '2026-10-01T09:00:00Z',
    decidedByUserId: 'rev',
    decidedByRole: 'Reviewer',
    decidedAt: '2026-10-02T09:00:00Z',
    isOverride: false,
    evidence: [{ label: 'Design approved', passed: true }],
    reviews: [],
    ...overrides,
  } as StageTransition;
}

test('the notice is shown when the backend reports submission evidence unavailable', () => {
  assert.equal(submissionEvidenceNote(approvedGate({ submissionEvidenceUnavailable: true })), SUBMISSION_EVIDENCE_UNAVAILABLE_NOTE);
});

test('no notice when the flag is false', () => {
  assert.equal(submissionEvidenceNote(approvedGate({ submissionEvidenceUnavailable: false })), null);
});

test('no notice when the flag is absent (an older API response)', () => {
  const older = approvedGate();
  delete older.submissionEvidenceUnavailable;
  assert.equal('submissionEvidenceUnavailable' in older, false);
  assert.equal(submissionEvidenceNote(older), null);
});

test('the notice speaks of availability, not of the record being legacy', () => {
  assert.match(SUBMISSION_EVIDENCE_UNAVAILABLE_NOTE, /not available/);
  assert.doesNotMatch(SUBMISSION_EVIDENCE_UNAVAILABLE_NOTE, /legacy|old record|before/i);
});
