// The nine workspace stages: order, and statuses derived only from Project.status and the real
// lifecycle state (pending request, evidence gaps). Nothing here may invent a status.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { activeStages, activeStagesLabel, completedWorkStages, currentStage, STAGES, stageByKey, stageChecks, stageGaps, stageStatus, outstandingWorkNote, APPROVED_PROGRESS_LABEL } from '../src/lib/stages.ts';

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
  assert.deepEqual(completedWorkStages(0), { done: 0, total: 7, attention: 0 });
  assert.deepEqual(completedWorkStages(3), { done: 3, total: 7, attention: 0 });
  assert.deepEqual(completedWorkStages(7), { done: 7, total: 7, attention: 0 });
});

// ---- Gap attribution: which failing exit checks make which stage "Needs attention" -------------

const fail = (label: string, ...areas: string[]) => ({
  label,
  passed: false,
  detail: `${label} is not met.`,
  guidance: { blockers: areas.map((area) => ({ destination: { area } })) },
});
const pass = (label: string) => ({ label, passed: true, detail: 'Met.' });

test('a completed project is not 100% complete while a stage has an evidence gap', () => {
  const signals = { pendingTransition: null, exitChecks: [], evidenceGaps: [{ stage: 3, checks: [pass('Solution design approved'), fail('API design approved', 'design')] }] };
  assert.equal(stageStatus(s('design'), 7, signals), 'attention');
  assert.deepEqual(completedWorkStages(7, signals), { done: 6, total: 7, attention: 1 });
  assert.deepEqual(
    stageGaps(s('design'), 7, signals).map((g) => g.check.label),
    ['API design approved'],
  );
  // Once the backend reports the check passing again, the stage is completed again.
  assert.equal(stageStatus(s('design'), 7, { pendingTransition: null, exitChecks: [], evidenceGaps: [] }), 'completed');
});

test('a shared lifecycle status sends each failing check to the stage its guidance points to', () => {
  // Testing (6) is served by Testing and Traceability. A failed test is Testing's; a trace gap is Traceability's.
  const failedTests = { evidenceGaps: [{ stage: 6, checks: [fail('Test cases passed', 'testing'), pass('Requirements fully traced')] }] };
  assert.equal(stageStatus(s('testing'), 7, failedTests), 'attention');
  assert.equal(stageStatus(s('traceability'), 7, failedTests), 'completed');
  const traceGaps = { evidenceGaps: [{ stage: 6, checks: [pass('Test cases passed'), fail('Requirements fully traced', 'traceability')] }] };
  assert.equal(stageStatus(s('testing'), 7, traceGaps), 'completed');
  assert.equal(stageStatus(s('traceability'), 7, traceGaps), 'attention');
  // Work in neither sharer (QA sign-off on the lifecycle panel) stays with the first sharer.
  const signOff = { evidenceGaps: [{ stage: 6, checks: [fail('QA sign-off recorded', 'lifecycle')] }] };
  assert.equal(stageStatus(s('testing'), 7, signOff), 'attention');
  assert.equal(stageStatus(s('traceability'), 7, signOff), 'completed');
  // Analysis (2) is shared by Analysis and Validation the same way.
  const openQuestions = { evidenceGaps: [{ stage: 2, checks: [fail('Clarification questions answered', 'validation')] }] };
  assert.equal(stageStatus(s('analysis'), 4, openQuestions), 'completed');
  assert.equal(stageStatus(s('validation'), 4, openQuestions), 'attention');
});

test('a check whose fix lives in another stage still belongs to the lifecycle status it gates', () => {
  // Design's "baseline intact" check fails because a requirement changed; the fix is in Analysis,
  // but it is Design's exit check, so Design (not Analysis) needs attention and links to Analysis.
  const signals = { evidenceGaps: [{ stage: 3, checks: [fail('Approved baseline unchanged', 'analysis')] }] };
  assert.equal(stageStatus(s('design'), 5, signals), 'attention');
  assert.equal(stageStatus(s('analysis'), 5, signals), 'completed');
});

test('a gap in an earlier status a stage serves shows even while a later one is current', () => {
  // Development serves Approved (4) and Implementation (5).
  const signals = { evidenceGaps: [{ stage: 4, checks: [fail('Implementation tasks approved', 'development')] }] };
  assert.equal(stageStatus(s('development'), 5, signals), 'attention');
});

test('the current status exit checks are to-dos of the stages serving it, never "Needs attention"', () => {
  const signals = { pendingTransition: null, evidenceGaps: [], exitChecks: [fail('Test cases passed', 'testing'), fail('Requirements fully traced', 'traceability'), pass('Test cases approved')] };
  assert.equal(stageStatus(s('testing'), 6, signals), 'in-progress');
  assert.deepEqual(
    stageChecks(s('testing'), 6, signals).map((c) => [c.kind, c.check.label, c.check.passed]),
    [
      ['todo', 'Test cases passed', false],
      ['todo', 'Test cases approved', true],
    ],
  );
  assert.deepEqual(
    stageChecks(s('traceability'), 6, signals).map((c) => c.check.label),
    ['Requirements fully traced'],
  );
  // Tools and stages not reached have no checks.
  assert.deepEqual(stageChecks(s('copilot'), 6, signals), []);
  assert.deepEqual(stageChecks(s('design'), 2, signals), []);
});

// The real catalog (ExitCheckCatalog.cs) for the two shared lifecycle statuses: each failing check,
// the destination areas of the blockers it can report, and the stage it must appear under.
test('every exit check of a shared lifecycle status appears under the stage its fix instructions point to', () => {
  const cases: [number, string, string[], string][] = [
    // Analysis (2): shared by Analysis and Validation.
    [2, 'AI requirement analysis run', ['analysis'], 'analysis'],
    [2, 'Clarification questions answered', ['validation'], 'validation'],
    [2, 'Functional requirements approved', ['analysis'], 'analysis'],
    [2, 'Non-functional requirements approved', ['analysis'], 'analysis'],
    [2, 'Business rules approved', ['analysis'], 'analysis'],
    [2, 'User stories approved', ['analysis'], 'analysis'],
    [2, 'Acceptance criteria approved', ['analysis'], 'analysis'],
    [2, 'Quality analysis run', ['validation'], 'validation'],
    [2, 'Conflict detection run', ['validation'], 'validation'],
    [2, 'Conflicts resolved', ['validation'], 'validation'],
    [2, 'Every functional requirement has a story and acceptance criteria', ['analysis'], 'analysis'],
    // Testing (6): shared by Testing and Traceability.
    [6, 'Test cases approved', ['testing'], 'testing'],
    [6, 'Every approved functional requirement has an approved test case', ['testing'], 'testing'],
    [6, 'Test cases passed', ['testing'], 'testing'],
    [6, 'Functional requirements verified', ['development', 'testing'], 'testing'],
    [6, 'Requirements fully traced', ['traceability'], 'traceability'],
    [6, 'No open impact notices', ['testing'], 'testing'],
    // Fixed outside both sharers: stays with the stage that owns the lifecycle status (the gate).
    [6, 'Conflicts resolved', ['validation'], 'testing'],
    [6, 'Requirements cite verified sources', ['analysis'], 'testing'],
    [6, 'Functional requirements verified', ['development'], 'testing'],
  ];
  for (const [status, label, areas, expected] of cases) {
    const signals = { evidenceGaps: [{ stage: status, checks: [fail(label, ...areas)] }] };
    const shown = STAGES.filter((st) => stageGaps(st, 7, signals).length > 0).map((st) => st.key);
    assert.deepEqual(shown, [expected], `${label} (${areas.join(', ')})`);
  }
});

test('outstanding work is reported beside an approved 100%, never folded into it', () => {
  assert.equal(outstandingWorkNote(7, { evidenceGaps: [] }), null);
  const gaps = { evidenceGaps: [{ stage: 3, checks: [fail('API design approved', 'design')] }, { stage: 6, checks: [fail('Requirements fully traced', 'traceability')] }] };
  assert.equal(outstandingWorkNote(7, gaps), '2 stages need attention');
  assert.equal(APPROVED_PROGRESS_LABEL, 'Approved lifecycle progress');
});
