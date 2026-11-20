// Run with `npm test`. Covers the dashboard's display-only project health summary.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { projectHealth } from '../src/lib/projectHealth.ts';

const clean = { pendingReview: false, evidenceGaps: 0, conflicts: 0, impactNotices: 0 };

test('a Completed project is always Completed, whatever its signals', () => {
  assert.deepEqual(projectHealth(7, { pendingReview: true, evidenceGaps: 2, conflicts: 3, impactNotices: 1 }), { health: 'completed', reasons: [] });
  assert.deepEqual(projectHealth(7, null), { health: 'completed', reasons: [] });
});

test('a project without loaded signals is not assessed', () => {
  assert.equal(projectHealth(3, null).health, 'unknown');
});

test('a project with no open signals is on track', () => {
  assert.deepEqual(projectHealth(0, clean), { health: 'on-track', reasons: [] });
  assert.equal(projectHealth(6, { ...clean, conflicts: null, impactNotices: null }).health, 'on-track');
});

test('pending review, evidence gaps, conflicts and impact notices need attention', () => {
  assert.deepEqual(projectHealth(3, { pendingReview: true, evidenceGaps: 1, conflicts: 2, impactNotices: 1 }), {
    health: 'attention',
    reasons: ['Awaiting Reviewer approval', '1 evidence gap', '2 conflicts', '1 change to review'],
  });
  assert.equal(projectHealth(2, { ...clean, impactNotices: 3 }).health, 'attention');
});
