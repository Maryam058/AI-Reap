// Run with `npm test`. Covers the coverage-gap summary shown above the traceability matrix. Which
// artifacts are orphans is decided by the backend (TraceabilityGapsTests); these fixtures stand in for it.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { TraceabilityGaps, TraceabilityRow } from '../src/api/types.ts';
import { gapsOf, rowHasGap } from '../src/lib/traceabilityGaps.ts';

const ref = (code: string) => ({ id: code, code, title: code });

function row(code: string, parts: Partial<Record<'userStories' | 'implementationTasks' | 'testCases', string[]>> = {}): TraceabilityRow {
  return {
    functionalRequirementId: code,
    functionalRequirementCode: code,
    functionalRequirementTitle: code,
    businessObjectives: [],
    businessRules: [],
    userStories: (parts.userStories ?? []).map(ref),
    acceptanceCriteria: [],
    designArtifacts: [],
    dataEntities: [],
    apiSpecifications: [],
    implementationTasks: (parts.implementationTasks ?? []).map(ref),
    testCases: (parts.testCases ?? []).map(ref),
  };
}

test('a requirement with a story, a task and a test has no gap; missing any one is a gap', () => {
  assert.equal(rowHasGap(row('FR-1', { userStories: ['US-1'], implementationTasks: ['T-1'], testCases: ['TC-1'] })), false);
  assert.equal(rowHasGap(row('FR-2', { userStories: ['US-1'], implementationTasks: ['T-1'] })), true);
  assert.equal(rowHasGap(row('FR-3', { testCases: ['TC-1'], implementationTasks: ['T-1'] })), true);
});

test('the summary lists uncovered requirements, orphans by type and untraced NFRs', () => {
  const rows = [row('FR-1', { userStories: ['US-1'], testCases: ['TC-1'] }), row('FR-2')];
  const gaps: TraceabilityGaps = {
    orphans: [
      { id: 'TC-9', code: 'TC-9', title: 't', artifactType: 10, status: 0, missingLink: 'requirement' },
      { id: 'US-9', code: 'US-9', title: 'u', artifactType: 3, status: 0, missingLink: 'requirement' },
      { id: 'US-8', code: 'US-8', title: 'u', artifactType: 3, status: 0, missingLink: 'requirement' },
    ],
    untracedNonFunctionalRequirements: [ref('NFR-1')],
    nonFunctionalRequirementCount: 3,
  };

  const summary = gapsOf(rows, gaps);

  assert.deepEqual(summary.withoutStories.map((r) => r.code), ['FR-2']);
  assert.deepEqual(summary.withoutTests.map((r) => r.code), ['FR-2']);
  assert.deepEqual(summary.orphansByType.map(([type, list]) => [type, list.map((o) => o.code)]), [[3, ['US-9', 'US-8']], [10, ['TC-9']]]);
  assert.equal(summary.nfrCount, 3);
  assert.equal(summary.total, 1 + 1 + 3 + 1);
});

test('without the gaps response only the matrix gaps are counted', () => {
  const summary = gapsOf([row('FR-1')], null);
  assert.equal(summary.orphansByType.length, 0);
  assert.equal(summary.total, 2);
});

test('NFR coverage becomes matrix-shaped rows that carry only test cases', async () => {
  const { nfrTestRows } = await import('../src/lib/traceabilityGaps.ts');
  const rows = nfrTestRows({
    orphans: [],
    untracedNonFunctionalRequirements: [],
    nonFunctionalRequirementCount: 1,
    nonFunctionalTestCoverage: [{ id: 'n1', code: 'NFR-1', title: 'Fast search', category: 'Performance', testCases: [ref('TC-5')] }],
  });
  assert.equal(rows.length, 1);
  assert.equal(rows[0].functionalRequirementId, 'n1');
  assert.deepEqual(rows[0].testCases.map((t) => t.code), ['TC-5']);
  assert.deepEqual(rows[0].userStories, []);
  assert.deepEqual(nfrTestRows(null), []);
});

test('only the orphans among the given artifacts are reported for a source', async () => {
  const { orphansAmong } = await import('../src/lib/traceabilityGaps.ts');
  const gaps: TraceabilityGaps = {
    orphans: [
      { id: 'a', code: 'US-1', title: '', artifactType: 3, status: 0, missingLink: 'requirement' },
      { id: 'b', code: 'US-2', title: '', artifactType: 3, status: 0, missingLink: 'requirement' },
    ],
    untracedNonFunctionalRequirements: [],
    nonFunctionalRequirementCount: 0,
  };
  assert.deepEqual(orphansAmong(gaps, ['b', 'z']).map((o) => o.code), ['US-2']);
  assert.deepEqual(orphansAmong(null, ['b']), []);
});

test('an acceptance criterion is linked to a story; anything else to an FR or NFR', async () => {
  const { linkCandidates } = await import('../src/lib/traceabilityGaps.ts');
  const rows = [row('FR-1', { userStories: ['US-1'] })];
  const gaps: TraceabilityGaps = {
    orphans: [{ id: 'US-9', code: 'US-9', title: '', artifactType: 3, status: 0, missingLink: 'requirement' }],
    untracedNonFunctionalRequirements: [],
    nonFunctionalRequirementCount: 1,
    nonFunctionalTestCoverage: [{ id: 'NFR-1', code: 'NFR-1', title: '', category: null, testCases: [] }],
  };
  const criterion = { id: 'AC-1', code: 'AC-1', title: '', artifactType: 4, status: 0, missingLink: 'user story' };
  const story = { id: 'US-9', code: 'US-9', title: '', artifactType: 3, status: 0, missingLink: 'requirement' };

  assert.deepEqual(linkCandidates(criterion, rows, gaps).map((c) => c.code), ['US-1', 'US-9']);
  assert.deepEqual(linkCandidates(story, rows, gaps).map((c) => c.code), ['FR-1', 'NFR-1']);
});

test('a rejected requirement or story is never offered as a link target', async () => {
  const { linkCandidates } = await import('../src/lib/traceabilityGaps.ts');
  const rows = [row('FR-1', { userStories: ['US-1', 'US-2'] }), row('FR-2')];
  const gaps: TraceabilityGaps = {
    orphans: [],
    untracedNonFunctionalRequirements: [],
    nonFunctionalRequirementCount: 1,
    nonFunctionalTestCoverage: [{ id: 'NFR-1', code: 'NFR-1', title: '', category: null, testCases: [] }],
  };
  const story = { id: 'US-9', code: 'US-9', title: '', artifactType: 3, status: 0, missingLink: 'requirement' };
  const criterion = { id: 'AC-1', code: 'AC-1', title: '', artifactType: 4, status: 0, missingLink: 'user story' };
  const rejected = new Set(['FR-1', 'US-2']);

  assert.deepEqual(linkCandidates(story, rows, gaps, rejected).map((c) => c.code), ['FR-2', 'NFR-1']);
  assert.deepEqual(linkCandidates(criterion, rows, gaps, rejected).map((c) => c.code), ['US-1']);
});
