// Covers the Testing page's QA summary (TestQaSummary). It must agree with the existing rules it
// reuses: testResultGateMessage (can a result be recorded) and currentTestResult (QA evidence only).
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ARTIFACT_STATUS, ARTIFACT_TYPE_VALUES, WORK_OUTCOME, type ArtifactSummary, type ArtifactWorkRecord, type WorkOutcome } from '../src/api/types.ts';
import { itemPath } from '../src/lib/checkGuidance.ts';
import { openPath, qaState, summarizeQa } from '../src/lib/testQaSummary.ts';

let seq = 0;
function rec(outcome: WorkOutcome, recordedByRole: string | null): ArtifactWorkRecord {
  seq += 1;
  return {
    id: `r${seq}`,
    artifactId: 'a1',
    versionNumber: 1,
    outcome,
    recordedByUserId: 'u1',
    recordedByRole,
    recordedAt: new Date(2026, 0, 1, 0, seq).toISOString(),
  };
}

function tc(code: string, status: number, source: string | null = 'src1'): ArtifactSummary {
  return {
    id: `id-${code}`,
    projectId: 'p1',
    artifactType: ARTIFACT_TYPE_VALUES.TestCase,
    code,
    title: `Test ${code}`,
    priority: null,
    status,
    origin: 0,
    requirementSourceId: source,
    data: {},
    currentVersion: 1,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  };
}

const { TestPassed, TestFailed, TestBlocked } = WORK_OUTCOME;
const { Approved, Draft, UnderReview, AiGenerated, Rejected, Implemented, Verified } = ARTIFACT_STATUS;

test('all QA Passed: everything passed, nothing needs attention', () => {
  const cases = [tc('TC-1', Approved), tc('TC-2', Implemented), tc('TC-3', Verified)];
  const s = summarizeQa(cases, new Map(cases.map((c) => [c.id, [rec(TestPassed, 'QA')]])));
  assert.equal(s.total, 3);
  assert.equal(s.counts.passed, 3);
  assert.equal(s.attentionCount, 0);
  assert.deepEqual(s.attention, []);
});

test('QA Failed is Failed, not Passed', () => {
  assert.equal(qaState(Approved, [rec(TestFailed, 'QA'), rec(TestPassed, 'QA')]), 'failed');
});

test('QA Blocked is Blocked', () => {
  assert.equal(qaState(Approved, [rec(TestBlocked, 'QA')]), 'blocked');
});

test('no QA result is Not executed', () => {
  assert.equal(qaState(Approved, []), 'notExecuted');
});

test('an Administrator Passed record alone does not count as Passed', () => {
  assert.equal(qaState(Approved, [rec(TestPassed, 'Administrator')]), 'notExecuted');
  const c = tc('TC-1', Approved);
  const s = summarizeQa([c], new Map([[c.id, [rec(TestPassed, 'Administrator')]]]));
  assert.equal(s.counts.passed, 0);
  assert.equal(s.attention[0].items[0].administratorRecordOnly, true);
});

test('QA Failed followed by an Administrator Passed stays Failed', () => {
  // Newest first: the Administrator's Passed is newer, but only the QA record is evidence.
  assert.equal(qaState(Approved, [rec(TestPassed, 'Administrator'), rec(TestFailed, 'QA')]), 'failed');
});

test('records without a role are not evidence', () => {
  assert.equal(qaState(Approved, [rec(TestPassed, null)]), 'notExecuted');
});

test('Draft, Under Review, Rejected and AI Generated need approval, whatever their records say', () => {
  for (const status of [Draft, UnderReview, Rejected, AiGenerated]) {
    assert.equal(qaState(status, [rec(TestPassed, 'QA')]), 'needsApproval');
    assert.equal(qaState(status, undefined), 'needsApproval');
  }
});

test('an approved test case with no QA result needs QA execution', () => {
  const c = tc('TC-1', Approved);
  const s = summarizeQa([c], new Map([[c.id, []]]));
  assert.equal(s.attention.length, 1);
  assert.equal(s.attention[0].title, 'Needs QA execution');
  assert.equal(s.attention[0].items[0].label, 'Not executed');
});

test('records that could not be loaded are reported, not guessed', () => {
  const c = tc('TC-1', Approved);
  const s = summarizeQa([c], new Map());
  assert.equal(s.counts.unavailable, 1);
  assert.equal(s.counts.notExecuted, 0);
});

test('mixed test cases produce correct totals and groups', () => {
  const cases = [
    tc('TC-10', Approved), // passed
    tc('TC-2', Approved), // passed
    tc('TC-3', Approved), // failed
    tc('TC-4', Approved), // blocked
    tc('TC-5', Approved), // not executed (Administrator Passed only)
    tc('TC-6', Draft), // needs approval
    tc('TC-7', UnderReview), // needs approval
  ];
  const records = new Map<string, ArtifactWorkRecord[]>([
    ['id-TC-10', [rec(TestPassed, 'QA')]],
    ['id-TC-2', [rec(TestPassed, 'QA'), rec(TestFailed, 'QA')]],
    ['id-TC-3', [rec(TestPassed, 'Administrator'), rec(TestFailed, 'QA')]],
    ['id-TC-4', [rec(TestBlocked, 'QA')]],
    ['id-TC-5', [rec(TestPassed, 'Administrator')]],
  ]);
  const s = summarizeQa(cases, records);
  assert.equal(s.total, 7);
  assert.deepEqual(s.counts, { passed: 2, failed: 1, blocked: 1, notExecuted: 1, needsApproval: 2, unavailable: 0 });
  assert.equal(s.attentionCount, 5);
  assert.deepEqual(
    s.attention.map((g) => [g.title, g.items.map((i) => i.testCase.code)]),
    [
      ['Needs approval', ['TC-6', 'TC-7']],
      ['Needs QA execution', ['TC-5']],
      ['Failed', ['TC-3']],
      ['Blocked', ['TC-4']],
    ],
  );
  assert.deepEqual(
    s.attention[0].items.map((i) => i.label),
    ['Draft', 'Under Review'],
  );
});

test('no test cases: empty summary', () => {
  const s = summarizeQa([], new Map());
  assert.equal(s.total, 0);
  assert.equal(s.attentionCount, 0);
});

test('Open uses the existing test-case deep link (source + focus on the Testing page)', () => {
  const c = tc('TC-9', Approved, 'source-9');
  const path = openPath('p1', c);
  assert.equal(
    path,
    itemPath('p1', { artifactId: c.id, code: c.code, title: c.title, artifactType: c.artifactType, status: c.status, requirementSourceId: 'source-9' }),
  );
  assert.match(path, /\/testing\?/);
  assert.match(path, /source=source-9/);
  assert.match(path, /focus=id-TC-9/);
});
