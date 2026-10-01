// Imported with its extension so `node --test` can load this module directly (tests/testQaSummary.test.ts).
import { ARTIFACT_STATUS_LABELS, WORK_OUTCOME, type ArtifactSummary, type ArtifactWorkRecord } from '../api/types.ts';
import { itemPath } from './checkGuidance.ts';
import { testResultGateMessage } from './testCaseReview.ts';
import { currentTestResult } from './workRecordEvidence.ts';

/**
 * The Testing page's QA summary. Presentation only: it defines no rule of its own. Whether a test
 * case can be run is testResultGateMessage (approved, implemented or verified); its result is
 * currentTestResult (the latest QA record - an Administrator's record is never evidence).
 */

export type QaState = 'passed' | 'failed' | 'blocked' | 'notExecuted' | 'needsApproval' | 'unavailable';

/** The attention groups, in the order they are listed. */
export const ATTENTION_GROUPS: readonly { state: Exclude<QaState, 'passed'>; title: string }[] = [
  { state: 'needsApproval', title: 'Needs approval' },
  { state: 'notExecuted', title: 'Needs QA execution' },
  { state: 'failed', title: 'Failed' },
  { state: 'blocked', title: 'Blocked' },
  { state: 'unavailable', title: 'Result unavailable' },
];

/**
 * `records` is the API's work-record list (newest first), or undefined when it could not be loaded.
 * Records are not needed - and not read - while the test case still needs approval.
 */
export function qaState(status: number, records: readonly ArtifactWorkRecord[] | undefined): QaState {
  if (testResultGateMessage(status) !== null) return 'needsApproval';
  if (records === undefined) return 'unavailable';
  switch (currentTestResult(records)) {
    case WORK_OUTCOME.TestPassed:
      return 'passed';
    case WORK_OUTCOME.TestFailed:
      return 'failed';
    case WORK_OUTCOME.TestBlocked:
      return 'blocked';
    default:
      return 'notExecuted';
  }
}

/** Whether a result can be recorded, so its work records are worth loading. */
export function isExecutable(status: number): boolean {
  return testResultGateMessage(status) === null;
}

export interface QaSummaryItem {
  testCase: ArtifactSummary;
  state: QaState;
  /** Short state text for the list row, e.g. "Under Review" or "Not executed". */
  label: string;
  /** The newest record is an Administrator's (kept for audit, not QA evidence). */
  administratorRecordOnly: boolean;
}

export interface QaSummary {
  total: number;
  counts: Record<QaState, number>;
  /** Every test case that is not QA-passed, grouped as in ATTENTION_GROUPS. */
  attention: { state: Exclude<QaState, 'passed'>; title: string; items: QaSummaryItem[] }[];
  attentionCount: number;
}

function label(state: QaState, status: number): string {
  switch (state) {
    case 'needsApproval':
      return ARTIFACT_STATUS_LABELS[status] ?? 'Not approved';
    case 'notExecuted':
      return 'Not executed';
    case 'failed':
      return 'Failed';
    case 'blocked':
      return 'Blocked';
    case 'unavailable':
      return 'Result could not be loaded';
    default:
      return 'Passed';
  }
}

/** `recordsById` holds each executable test case's work records; a missing entry means they could not be loaded. */
export function summarizeQa(testCases: readonly ArtifactSummary[], recordsById: ReadonlyMap<string, readonly ArtifactWorkRecord[]>): QaSummary {
  const counts: Record<QaState, number> = { passed: 0, failed: 0, blocked: 0, notExecuted: 0, needsApproval: 0, unavailable: 0 };
  const items: QaSummaryItem[] = testCases.map((testCase) => {
    const records = recordsById.get(testCase.id);
    const state = qaState(testCase.status, records);
    counts[state] += 1;
    const newest = records?.[0];
    return {
      testCase,
      state,
      label: label(state, testCase.status),
      administratorRecordOnly: state !== 'needsApproval' && newest !== undefined && newest.recordedByRole !== 'QA',
    };
  });
  const byCode = (a: QaSummaryItem, b: QaSummaryItem) => a.testCase.code.localeCompare(b.testCase.code, undefined, { numeric: true });
  const attention = ATTENTION_GROUPS.map((g) => ({ ...g, items: items.filter((i) => i.state === g.state).sort(byCode) })).filter((g) => g.items.length > 0);
  return { total: testCases.length, counts, attention, attentionCount: testCases.length - counts.passed };
}

/** Where "Open" goes: the existing deep link to the test case's card (the same one exit-check guidance uses). */
export function openPath(projectId: string, testCase: ArtifactSummary): string {
  return itemPath(projectId, {
    artifactId: testCase.id,
    code: testCase.code,
    title: testCase.title,
    artifactType: testCase.artifactType,
    status: testCase.status,
    requirementSourceId: testCase.requirementSourceId,
  });
}
