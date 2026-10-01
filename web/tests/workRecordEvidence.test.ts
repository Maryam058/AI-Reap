// Covers which test result WorkRecordPanel shows as current (its button is then not an action).
// The backend's evidence rules themselves are tested in AiReap.Tests.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { WORK_OUTCOME, type ArtifactWorkRecord, type WorkOutcome } from '../src/api/types.ts';
import { currentTestResult, latestEvidence } from '../src/lib/workRecordEvidence.ts';
import { roleGuard } from '../src/lib/roleGuard.ts';

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

const { TestPassed, TestFailed, TestBlocked } = WORK_OUTCOME;
const ALL = [TestPassed, TestFailed, TestBlocked];
// A button is clickable unless it is the current result (or a request is in flight).
const enabled = (records: ArtifactWorkRecord[]) => ALL.filter((o) => o !== currentTestResult(records));

test('latest QA result Passed makes Passed the current (disabled) result', () => {
  assert.equal(currentTestResult([rec(TestPassed, 'QA')]), TestPassed);
});

test('latest QA result Failed makes Failed the current (disabled) result', () => {
  assert.equal(currentTestResult([rec(TestFailed, 'QA'), rec(TestPassed, 'QA')]), TestFailed);
});

test('latest QA result Blocked makes Blocked the current (disabled) result', () => {
  assert.equal(currentTestResult([rec(TestBlocked, 'QA'), rec(TestFailed, 'QA')]), TestBlocked);
});

test('the other results stay available so QA can change the result', () => {
  assert.deepEqual(enabled([rec(TestPassed, 'QA')]), [TestFailed, TestBlocked]);
  assert.deepEqual(enabled([rec(TestFailed, 'QA')]), [TestPassed, TestBlocked]);
  assert.deepEqual(enabled([rec(TestBlocked, 'QA')]), [TestPassed, TestFailed]);
});

test('no records: every result is available', () => {
  assert.equal(currentTestResult([]), null);
  assert.equal(currentTestResult(null), null);
  assert.deepEqual(enabled([]), ALL);
});

test("an Administrator's non-evidence result does not disable the matching QA action", () => {
  // Only an Administrator record: QA must still be able to record Passed as evidence.
  assert.equal(currentTestResult([rec(TestPassed, 'Administrator')]), null);
  assert.deepEqual(enabled([rec(TestPassed, 'Administrator')]), ALL);
  // Administrator's newer Passed over QA's Failed: Failed stays current, Passed stays available.
  const records = [rec(TestPassed, 'Administrator'), rec(TestFailed, 'QA')];
  assert.equal(currentTestResult(records), TestFailed);
  assert.deepEqual(enabled(records), [TestPassed, TestBlocked]);
});

test('records without a role are not evidence', () => {
  assert.equal(currentTestResult([rec(TestPassed, null)]), null);
});

test('latestEvidence picks the newest record made under the owner role', () => {
  const qa = rec(TestFailed, 'QA');
  assert.equal(latestEvidence([rec(TestPassed, 'Administrator'), qa, rec(TestPassed, 'QA')], 'QA'), qa);
  const dev = rec(WORK_OUTCOME.TaskCompleted, 'Developer');
  assert.equal(latestEvidence([rec(WORK_OUTCOME.TaskReopened, 'Administrator'), dev], 'Developer'), dev);
});

test('role guard for recording a test result is unchanged', () => {
  assert.deepEqual(roleGuard('QA', ['QA']), { kind: 'allowed' });
  assert.deepEqual(roleGuard('QA', ['Administrator', 'QA']), { kind: 'allowed' });
  assert.deepEqual(roleGuard('QA', ['Administrator']), { kind: 'administrator' });
  assert.deepEqual(roleGuard('QA', ['Developer']), { kind: 'blocked' });
});
