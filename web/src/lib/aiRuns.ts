// Imported with its extension so `node --test` can load this module directly (tests/aiRuns.test.ts).
import type { AiChunkRun, AiPlanResult } from '../api/types.ts';

/**
 * Wording and visibility for batched AI runs (one requirement source processed in bounded batches).
 * Every rule here is the backend's (AiChunkRunService); this only decides what to show. Nothing is
 * offered while batch mode is off.
 */

/** Offer "Process in batches" only when batch mode is on, a run could start, and the normal run would be refused for its part count. */
export function shouldOfferBatch(plan: AiPlanResult | null): boolean {
  const batch = plan?.batch;
  if (!plan || !batch?.enabled || !batch.canStart || batch.activeRun) return false;
  return plan.providers.some((p) => p.reasons.some((r) => r.code === 'TooManyParts'));
}

/** "13 parts in 2 actions of up to 8 (sized for Groq)". */
export function batchOfferText(plan: AiPlanResult): string {
  const b = plan.batch!;
  const parts = b.parts ?? 0;
  const actions = b.actions ?? 0;
  return `${parts} parts in ${actions} action${actions === 1 ? '' : 's'} of up to ${b.partsPerAction} (sized for ${b.budgetProvider}). Nothing is saved until you save the finished run.`;
}

export function runStatusLabel(run: AiChunkRun): string {
  switch (run.status) {
    case 'Running':
      return run.lastError ? 'Paused' : 'In progress';
    case 'Staged':
      return 'Ready to save';
    case 'Committed':
      return 'Saved';
    case 'Failed':
      return 'Failed';
    case 'Superseded':
      return 'Out of date';
    default:
      return 'Abandoned';
  }
}

/** "Part 8 of 13 done" plus why it stopped, if it did. */
export function runProgressText(run: AiChunkRun): string {
  const done = `${run.succeededParts} of ${run.partCount} parts done`;
  if (run.status === 'Running' && run.lastError) return `${done} · paused: ${run.lastError}`;
  if (run.status === 'Failed' || run.status === 'Superseded') return `${done} · ${run.lastError ?? ''}`.trim();
  return done;
}

/** How many parts the next Continue will send. */
export function nextBatchSize(run: AiChunkRun): number {
  return Math.min(run.partsPerAction, run.partCount - run.succeededParts);
}

export function canContinue(run: AiChunkRun): boolean {
  return run.status === 'Running' && !run.leaseActive && nextBatchSize(run) > 0;
}

export function canSave(run: AiChunkRun): boolean {
  return run.status === 'Staged' && !run.leaseActive;
}

export function canAbandon(run: AiChunkRun): boolean {
  return (run.status === 'Running' || run.status === 'Staged') && !run.leaseActive;
}

/** After saving: "Saved 39 items; 3 already existed and were not added again." */
export function savedText(run: AiChunkRun): string {
  const created = run.result?.createdArtifactIds.length ?? 0;
  const skipped = run.result?.skippedDuplicates ?? 0;
  return skipped > 0
    ? `Saved ${created} item${created === 1 ? '' : 's'}; ${skipped} already existed and were not added again.`
    : `Saved ${created} item${created === 1 ? '' : 's'}.`;
}
