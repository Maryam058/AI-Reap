// Run with `npm test`. Covers what the batched-run panel shows. The rules (part limits, leases,
// stale detection, atomic save) are the backend's and are tested in AiReap.Tests (AiChunkRunTests);
// the fixtures below stand in for its answers.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { AiChunkRun, AiPlanBatch, AiPlanProvider, AiPlanResult } from '../src/api/types.ts';
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
} from '../src/lib/aiRuns.ts';

function run(overrides: Partial<AiChunkRun> = {}): AiChunkRun {
  return {
    id: 'r1',
    requirementSourceId: 's1',
    operation: 'analyze',
    status: 'Running',
    partCount: 13,
    succeededParts: 0,
    failedParts: 0,
    pendingParts: 13,
    partsPerAction: 8,
    remainingActions: 2,
    budgetProvider: 'Groq',
    lastError: null,
    lastErrorRetryable: false,
    leaseActive: false,
    startedAt: '2026-10-15T09:00:00Z',
    updatedAt: '2026-10-15T09:00:00Z',
    completedAt: null,
    result: null,
    parts: [],
    ...overrides,
  };
}

const groqBlocked = {
  provider: 'Groq',
  reasons: [{ code: 'TooManyParts', severity: 'Blocking', message: 'Groq would need 13 parts; at most 8.' }],
} as unknown as AiPlanProvider;

function plan(batch: Partial<AiPlanBatch> | null, providers: AiPlanProvider[] = [groqBlocked]): AiPlanResult {
  return {
    providers,
    batch: batch === null ? null : {
      enabled: true,
      maxRunParts: 80,
      budgetProvider: 'Groq',
      partsPerAction: 8,
      parts: 13,
      actions: 2,
      canStart: true,
      reason: null,
      activeRun: null,
      ...batch,
    },
  } as unknown as AiPlanResult;
}

test('batches are offered only when batch mode is on, a run can start, and the normal run hits the part limit', () => {
  assert.equal(shouldOfferBatch(plan({})), true);
  assert.equal(shouldOfferBatch(plan(null)), false);
  assert.equal(shouldOfferBatch(plan({ enabled: false })), false);
  assert.equal(shouldOfferBatch(plan({ canStart: false })), false);
  assert.equal(shouldOfferBatch(plan({ activeRun: run() })), false);
  assert.equal(shouldOfferBatch(plan({}, [])), false); // nothing blocked: run it normally
  assert.equal(shouldOfferBatch(null), false);
});

test('the offer states parts, actions, the per-action limit and that nothing is saved early', () => {
  assert.equal(
    batchOfferText(plan({})),
    '13 parts in 2 actions of up to 8 (sized for Groq). Nothing is saved until you save the finished run.',
  );
});

test('a paused run says how far it got and why it stopped', () => {
  const paused = run({ succeededParts: 3, failedParts: 1, lastError: 'Groq daily token limit reached.', lastErrorRetryable: true });
  assert.equal(runStatusLabel(paused), 'Paused');
  assert.equal(runProgressText(paused), '3 of 13 parts done · paused: Groq daily token limit reached.');
  assert.equal(canContinue(paused), true);
  assert.equal(nextBatchSize(paused), 8);
  assert.equal(nextBatchSize(run({ succeededParts: 8 })), 5);
});

test('only a staged run can be saved; nothing can be done while another request holds it', () => {
  assert.equal(canSave(run({ status: 'Staged', succeededParts: 13 })), true);
  assert.equal(canSave(run()), false);
  assert.equal(canContinue(run({ leaseActive: true })), false);
  assert.equal(canAbandon(run({ leaseActive: true })), false);
  assert.equal(canAbandon(run({ status: 'Committed' })), false);
});

test('a saved run reports created items and skipped duplicates', () => {
  assert.equal(savedText(run({ status: 'Committed', result: { createdArtifactIds: ['a', 'b'], skippedDuplicates: 0 } })), 'Saved 2 items.');
  assert.equal(
    savedText(run({ status: 'Committed', result: { createdArtifactIds: ['a'], skippedDuplicates: 3 } })),
    'Saved 1 item; 3 already existed and were not added again.',
  );
  assert.equal(runStatusLabel(run({ status: 'Superseded' })), 'Out of date');
});
