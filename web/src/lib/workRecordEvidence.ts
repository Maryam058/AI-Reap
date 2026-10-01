// Imported with its extension so `node --test` can load this module directly (tests/workRecordEvidence.test.ts).
import { WORK_OUTCOME, type ArtifactWorkRecord, type WorkOutcome } from '../api/types.ts';

/**
 * Mirrors LifecycleEvidenceService.LatestOutcomes: only a record made under the owner role
 * (Developer for tasks, QA for tests) is evidence. An Administrator's record stays in the history
 * but is ignored. `records` is the API's list, newest first.
 */
export function latestEvidence(records: readonly ArtifactWorkRecord[] | null, ownerRole: string): ArtifactWorkRecord | null {
  return records?.find((r) => r.recordedByRole === ownerRole) ?? null;
}

const TEST_RESULTS: readonly WorkOutcome[] = [WORK_OUTCOME.TestPassed, WORK_OUTCOME.TestFailed, WORK_OUTCOME.TestBlocked];

/**
 * The test result that is already current, so recording it again would change nothing: the outcome
 * of the latest QA record. An Administrator's record never makes a result current, so QA can still
 * record that result as evidence.
 */
export function currentTestResult(records: readonly ArtifactWorkRecord[] | null): WorkOutcome | null {
  const outcome = latestEvidence(records, 'QA')?.outcome;
  return outcome !== undefined && TEST_RESULTS.includes(outcome) ? outcome : null;
}
