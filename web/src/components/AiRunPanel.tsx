import { useState } from 'react';
import { aiRunsApi } from '../api/aiRuns';
import type { AiChunkRun, AiPlanOperation, AiPlanResult } from '../api/types';
import { apiErrorText } from '../lib/errors';
import {
  batchOfferText,
  canAbandon,
  canContinue,
  canSave,
  nextBatchSize,
  runProgressText,
  runStatusLabel,
  savedText,
  shouldOfferBatch,
} from '../lib/aiRuns';

/**
 * Batched processing of the selected requirement source (batch mode only): offered when the normal
 * run would be refused for its part count, then Continue (one bounded batch per click), Save (all
 * at once) or Abandon. Shows nothing while batch mode is off.
 */
export function AiRunPanel({
  sourceId,
  operation,
  plan,
  token,
  canWrite,
  onChanged,
  onSaved,
}: {
  sourceId: string;
  operation: AiPlanOperation;
  plan: AiPlanResult | null;
  token: string | null;
  canWrite: boolean;
  onChanged: () => void;
  onSaved: () => void;
}) {
  const [run, setRun] = useState<AiChunkRun | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  // The latest answer from an action wins over the plan's (possibly older) copy of the same run.
  const planRun = plan?.batch?.activeRun ?? null;
  const current = run && run.requirementSourceId === sourceId && run.operation === operation && (!planRun || run.id === planRun.id) ? run : planRun;

  const act = async (key: string, fn: () => Promise<AiChunkRun>, saved = false) => {
    setBusy(key);
    setError(null);
    try {
      const next = await fn();
      setRun(next);
      if (saved) onSaved();
    } catch (err) {
      setError(apiErrorText(err, 'The batched run could not be updated.'));
    } finally {
      setBusy(null);
      onChanged();
    }
  };

  if (!plan?.batch?.enabled) return null;

  // A run that ended without saving: say how, and offer a new one.
  const ended = current && (current.status === 'Abandoned' || current.status === 'Failed' || current.status === 'Superseded');
  if (!current || ended) {
    const offer = canWrite && shouldOfferBatch(plan);
    if (!offer && !ended) return null;
    return (
      <div className="ai-run">
        {ended && (
          <p className="ai-run-text">
            Batched run {runStatusLabel(current).toLowerCase()}. {runProgressText(current)}
          </p>
        )}
        {offer && <p className="ai-run-text">{batchOfferText(plan)}</p>}
        {offer && (
        <button
          type="button"
          className="btn btn-secondary btn-sm"
          disabled={busy !== null}
          onClick={() => act('start', () => aiRunsApi.start(sourceId, operation, token))}
        >
          {busy === 'start' ? 'Preparing…' : 'Process in batches'}
        </button>
        )}
        {error && <p className="ai-run-error">{error}</p>}
      </div>
    );
  }

  return (
    <div className={`ai-run ai-run-${current.status.toLowerCase()}`}>
      <div className="ai-run-head">
        <span className="ai-plan-chip">{runStatusLabel(current)}</span>
        <span className="ai-run-text">
          {current.status === 'Committed' ? savedText(current) : runProgressText(current)}
        </span>
      </div>
      <progress max={current.partCount} value={current.succeededParts} aria-label="Batched run progress" />
      {canWrite && (
        <div className="ai-run-actions">
          {canContinue(current) && (
            <button
              type="button"
              className="btn btn-secondary btn-sm"
              disabled={busy !== null}
              onClick={() => act('continue', () => aiRunsApi.continue(current.id, token))}
            >
              {busy === 'continue' ? 'Processing…' : `Continue (${nextBatchSize(current)} part${nextBatchSize(current) === 1 ? '' : 's'})`}
            </button>
          )}
          {canSave(current) && (
            <button
              type="button"
              className="btn btn-primary btn-sm"
              disabled={busy !== null}
              onClick={() => act('commit', () => aiRunsApi.commit(current.id, token), true)}
            >
              {busy === 'commit' ? 'Saving…' : 'Save results'}
            </button>
          )}
          {canAbandon(current) && (
            <button
              type="button"
              className="btn btn-ghost btn-sm"
              disabled={busy !== null}
              onClick={() => act('abandon', () => aiRunsApi.abandon(current.id, token))}
            >
              Abandon
            </button>
          )}
        </div>
      )}
      {current.leaseActive && <p className="ai-run-text">Another request is processing this run…</p>}
      {error && <p className="ai-run-error">{error}</p>}
    </div>
  );
}
