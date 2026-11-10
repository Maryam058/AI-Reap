// The nine workspace stages: order, and statuses derived only from Project.status and the real
// lifecycle state (pending request, evidence gaps). Nothing here may invent a status.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { activeStages, activeStagesLabel, completedWorkStages, currentStage, STAGES, stageByKey, stageStatus } from '../src/lib/stages.ts';

const s = (key: string) => stageByKey(key)!;

test('nine stages in the documented order, on the existing routes', () => {
  assert.deepEqual(
    STAGES.map((x) => x.name),
    ['Gathering', 'Analysis', 'Validation', 'Design', 'Development', 'Testing', 'Traceability', 'AI Copilot', 'Review'],
  );
  assert.deepEqual(
    STAGES.map((x) => x.segment),
    ['gathering', 'analysis', 'validation', 'design', 'development', 'testing', 'traceability', 'copilot', 'review'],
  );
  assert.deepEqual(
    STAGES.map((x) => x.number),
    [1, 2, 3, 4, 5, 6, 7, 8, 9],
  );
});

test('work stage statuses follow Project.status', () => {
  // Status 2 = Analysis: Gathering passed; Analysis and Validation both serve it; Design onwards not started.
  assert.equal(stageStatus(s('gathering'), 2), 'completed');
  assert.equal(stageStatus(s('analysis'), 2), 'in-progress');
  assert.equal(stageStatus(s('validation'), 2), 'in-progress');
  assert.equal(stageStatus(s('design'), 2), 'not-started');
  // Development serves Approved (4) and Implementation (5): in progress in both, completed only after.
  assert.equal(stageStatus(s('development'), 4), 'in-progress');
  assert.equal(stageStatus(s('development'), 5), 'in-progress');
  assert.equal(stageStatus(s('development'), 6), 'completed');
});

test('tools have no completion of their own', () => {
  for (const status of [0, 3, 6]) {
    assert.equal(stageStatus(s('copilot'), status), 'available');
    assert.equal(stageStatus(s('review'), status), 'available');
  }
  // The Review becomes the next step once the lifecycle is completed.
  assert.equal(stageStatus(s('review'), 7), 'ready');
  assert.equal(stageStatus(s('copilot'), 7), 'available');
});

test('a pending Reviewer request and evidence gaps come from the lifecycle state only', () => {
  const pending = { pendingTransition: { fromStatus: 3, toStatus: 4 }, evidenceGaps: [] };
  assert.equal(stageStatus(s('design'), 3, pending), 'awaiting');
  assert.equal(stageStatus(s('design'), 3, null), 'in-progress');
  const gaps = { pendingTransition: null, evidenceGaps: [{ stage: 2 }] };
  assert.equal(stageStatus(s('analysis'), 4, gaps), 'attention');
  assert.equal(stageStatus(s('validation'), 4, gaps), 'attention');
  assert.equal(stageStatus(s('gathering'), 4, gaps), 'completed');
});

test('the current stage is where the work for the lifecycle status happens', () => {
  assert.equal(currentStage(0).key, 'gathering');
  assert.equal(currentStage(2).key, 'analysis');
  assert.equal(activeStagesLabel(2), 'Analysis & Validation');
  assert.equal(activeStagesLabel(6), 'Testing & Traceability');
  assert.equal(currentStage(7).key, 'review');
  assert.deepEqual(
    activeStages(7).map((x) => x.key),
    ['review'],
  );
});

test('completed work stages are counted, tools excluded', () => {
  assert.deepEqual(completedWorkStages(0), { done: 0, total: 7 });
  assert.deepEqual(completedWorkStages(3), { done: 3, total: 7 });
  assert.deepEqual(completedWorkStages(7), { done: 7, total: 7 });
});
