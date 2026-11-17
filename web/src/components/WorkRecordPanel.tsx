import { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { workRecordsApi } from '../api/workRecords';
import { ARTIFACT_STATUS, ARTIFACT_TYPE_VALUES, WORK_OUTCOME, type ArtifactSummary, type ArtifactWorkRecord, type WorkOutcome } from '../api/types';
import { personLabel, useMemberNames } from '../hooks/useMemberNames';
import { apiErrorText } from '../lib/errors';
import { formatRelativeTime } from '../lib/format';
import { IconCheck } from './icons';
import { useRoleGuard } from '../hooks/useRoleGuard';
import { RoleRequiredAlert } from './RoleRequiredAlert';
import { currentTestResult, latestEvidence } from '../lib/workRecordEvidence';
import { testResultGateMessage } from '../lib/testCaseReview';

const OUTCOME: Record<number, { label: string; tone: string }> = {
  [WORK_OUTCOME.TaskCompleted]: { label: 'Completed', tone: 'success' },
  [WORK_OUTCOME.TaskReopened]: { label: 'Reopened', tone: 'warning' },
  [WORK_OUTCOME.TestPassed]: { label: 'Passed', tone: 'success' },
  [WORK_OUTCOME.TestFailed]: { label: 'Failed', tone: 'danger' },
  [WORK_OUTCOME.TestBlocked]: { label: 'Blocked', tone: 'blocked' },
};

const TEST_RESULT_BUTTONS: { outcome: WorkOutcome; label: string; className: string }[] = [
  { outcome: WORK_OUTCOME.TestPassed, label: 'Passed', className: 'btn-primary' },
  { outcome: WORK_OUTCOME.TestFailed, label: 'Failed', className: 'btn-secondary btn-reject' },
  { outcome: WORK_OUTCOME.TestBlocked, label: 'Blocked', className: 'btn-secondary' },
];

/**
 * Implementation / verification evidence for one approved task or test case (§5 decision 7): a
 * Developer records a task as complete, QA records a test run's result. These work records - not
 * the artifact's approval - are what make a linked requirement Implemented or Verified and what
 * the Implementation and Testing exit checks read. Recording never edits, versions or re-approves
 * the artifact. The backend decides who may record; the buttons only follow its documented rule.
 */
export function WorkRecordPanel({ artifact, onRecorded }: { artifact: ArtifactSummary; onRecorded?: () => void }) {
  const { token, userId } = useAuth();
  const names = useMemberNames(artifact.projectId);
  const isTask = artifact.artifactType === ARTIFACT_TYPE_VALUES.ImplementationTask;
  // Mirrors ArtifactWorkService: Developer (tasks) / QA (tests), or an Administrator. The API re-checks.
  const ownerRole = isTask ? 'Developer' : 'QA';
  const guard = useRoleGuard(ownerRole);
  const [records, setRecords] = useState<ArtifactWorkRecord[] | null>(null);
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [version, setVersion] = useState(0);
  const [attempted, setAttempted] = useState<WorkOutcome | null>(null);

  useEffect(() => {
    let cancelled = false;
    workRecordsApi
      .list(artifact.id, token)
      .then((r) => !cancelled && setRecords(r))
      .catch(() => !cancelled && setRecords([]));
    return () => {
      cancelled = true;
    };
  }, [artifact.id, token, version]);

  const record = useCallback(
    async (outcome: WorkOutcome) => {
      setBusy(true);
      setError(null);
      try {
        await workRecordsApi.record(artifact.id, outcome, note.trim() || undefined, token);
        setNote('');
        setVersion((v) => v + 1);
        onRecorded?.();
      } catch (err) {
        setError(apiErrorText(err, 'The result could not be recorded.'));
      } finally {
        setBusy(false);
      }
    },
    [artifact.id, note, token, onRecorded],
  );

  const approved =
    artifact.status === ARTIFACT_STATUS.Approved || artifact.status === ARTIFACT_STATUS.Implemented || artifact.status === ARTIFACT_STATUS.Verified;
  const asAdministrator = guard.decision.kind === 'administrator';
  const mayRecord = guard.decision.kind !== 'blocked';
  const latest = records && records.length > 0 ? records[0] : null; // newest first
  // Mirrors LifecycleEvidenceService.LatestOutcomes: only records made under the owner role are
  // evidence. An Administrator's record stays visible, but the exit checks ignore it.
  const isEvidence = (r: ArtifactWorkRecord) => r.recordedByRole === ownerRole;
  const evidence = latestEvidence(records, ownerRole);
  // The latest QA result is already recorded, so its button shows as the current result instead of an action.
  const currentResult = isTask ? null : currentTestResult(records);
  const current = evidence ? OUTCOME[evidence.outcome] : null;
  const unofficial = latest && !isEvidence(latest) ? OUTCOME[latest.outcome] : null;
  // The API records under the owner role whenever the user holds it, so an owner acts on the
  // evidence; an Administrator-only user acts on the latest record, their own included.
  const actingOn = asAdministrator ? latest : evidence;
  // Anyone may try; only the owner role records straight away, everyone else gets the RoleRequiredAlert.
  const attempt = (outcome: WorkOutcome) => {
    setAttempted(outcome);
    guard.attempt(() => void record(outcome));
  };
  const title = isTask ? 'Implementation' : 'Test execution';

  return (
    <div className="work-record" aria-label={`${title} evidence`}>
      <div className="work-record-head">
        <strong>{title}</strong>
        {records === null ? (
          <span className="muted">Loading…</span>
        ) : current ? (
          <span className={`status-pill tone-${current.tone}`}>
            {isTask ? current.label : `Last run: ${current.label}`}
          </span>
        ) : (
          <span className="status-pill tone-neutral">{isTask ? 'Not completed' : 'Not run yet'}</span>
        )}
      </div>
      {unofficial && (
        <p className="hint work-record-unofficial">
          Latest record ({unofficial.label}) was made by an Administrator and does not count as {isTask ? 'implementation' : 'test'} evidence.
          {` ${isTask ? 'A Developer' : 'QA'} must record it for the lifecycle checks to pass.`}
        </p>
      )}
      <p className="hint">
        {isTask
          ? 'Approving a task accepts the plan; it does not mean the work is done. A Developer records completion here, and the linked requirement becomes Implemented only when all of its tasks are recorded complete.'
          : 'Approving a test case accepts the test; it is not a test result. QA records each run here, and the linked requirement becomes Verified only when all of its tests have passed (after its tasks are complete).'}
      </p>

      {!approved ? (
        <p className="hint">{isTask ? 'This task must be approved before completion can be recorded.' : testResultGateMessage(artifact.status)}</p>
      ) : (
        <div className="work-record-actions">
          {mayRecord && (
            <input type="text" placeholder={isTask ? 'Note, e.g. PR or build reference (optional)' : 'Note, e.g. environment or defect (optional)'} value={note} onChange={(e) => setNote(e.target.value)} disabled={busy} aria-label="Work record note" />
          )}
          {isTask ? (
            actingOn?.outcome === WORK_OUTCOME.TaskCompleted ? (
              <button type="button" className="btn btn-secondary btn-sm" disabled={busy} onClick={() => attempt(WORK_OUTCOME.TaskReopened)}>
                Reopen task
              </button>
            ) : (
              <button type="button" className="btn btn-primary btn-sm" disabled={busy} onClick={() => attempt(WORK_OUTCOME.TaskCompleted)}>
                <IconCheck width={14} height={14} /> Mark complete
              </button>
            )
          ) : (
            TEST_RESULT_BUTTONS.map(({ outcome, label, className }) => {
              const isCurrent = currentResult === outcome;
              return (
                <button
                  key={outcome}
                  type="button"
                  className={`btn btn-sm ${className}${isCurrent ? ' is-current' : ''}`}
                  disabled={busy || isCurrent}
                  aria-pressed={isCurrent}
                  title={isCurrent ? `Current result: ${label}` : undefined}
                  onClick={() => attempt(outcome)}
                >
                  {isCurrent && <IconCheck width={14} height={14} />} {label}
                </button>
              );
            })
          )}
        </div>
      )}
      {guard.raised && (
        <RoleRequiredAlert
          requiredRole={ownerRole}
          onDismiss={guard.dismiss}
          administrator={{
            explanation: `As an Administrator you can still record this, but it is kept as an Administrator record and does not count as ${ownerRole} evidence. To move the stage on without it, use the Administrator override in the project lifecycle panel.`,
            // The backend deliberately accepts an Administrator's record (audited, not evidence): kept, but only as an explicit choice.
            action:
              attempted === null
                ? undefined
                : {
                    label: 'Record as Administrator (not evidence)',
                    busy,
                    onClick: () => {
                      guard.dismiss();
                      void record(attempted);
                    },
                  },
          }}
        />
      )}

      {error && <p className="field-error-text">{error}</p>}

      {records && records.length > 0 && (
        <details className="work-record-history">
          <summary>History ({records.length})</summary>
          <ul>
            {records.map((r) => (
              <li key={r.id}>
                <span className={`status-pill tone-${OUTCOME[r.outcome]?.tone ?? 'neutral'}`}>{OUTCOME[r.outcome]?.label ?? r.outcome}</span>
                <span>{personLabel(r.recordedByUserId, r.recordedByRole, names, userId)}</span>
                <span className="muted">
                  {formatRelativeTime(r.recordedAt)} · v{r.versionNumber}
                </span>
                {!isEvidence(r) && <span className="muted">not evidence</span>}
                {r.note && <span className="muted">“{r.note}”</span>}
              </li>
            ))}
          </ul>
        </details>
      )}
    </div>
  );
}
