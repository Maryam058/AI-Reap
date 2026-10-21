// Covers the Copilot's question-length check and reduced-context notice (CopilotPanel). The server's
// own limit and context fitting are tested in AiReap.Tests (CopilotContextFitterTests,
// LosslessStageBudgetTests).
import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { CopilotContextReport } from '../src/api/types.ts';
import {
  copilotContextNotice,
  DEFAULT_MAX_QUESTION_CHARS,
  effectiveMaxQuestionChars,
  questionLengthError,
} from '../src/lib/copilotContext.ts';

const unreduced: CopilotContextReport = {
  reduced: false,
  reason: null,
  provider: null,
  inputBudget: null,
  artifactsFull: 12,
  artifactsTruncated: 0,
  artifactsTitleOnly: 0,
  artifactsOmitted: 0,
  versionsOmitted: 0,
  impactCodesOmitted: 0,
  excerptsOmitted: 0,
};

test('a question of exactly the limit is accepted; one character more is rejected, never shortened', () => {
  assert.equal(DEFAULT_MAX_QUESTION_CHARS, 2000);
  assert.equal(questionLengthError('q'.repeat(2000), 2000), null);
  const error = questionLengthError('q'.repeat(2001), 2000);
  assert.equal(error, 'Your question is 2,001 characters long; the Copilot accepts at most 2,000. Shorten it to ask.');
  // The message never repeats the question itself.
  assert.ok(!error!.includes('qqq'));
});

test('the limit follows the server, and falls back to the default when the server value is unusable', () => {
  assert.equal(effectiveMaxQuestionChars(500), 500);
  assert.equal(questionLengthError('q'.repeat(501), effectiveMaxQuestionChars(500))?.startsWith('Your question is 501'), true);
  for (const bad of [0, -1, 1.5, '2000', null, undefined]) {
    assert.equal(effectiveMaxQuestionChars(bad), 2000);
  }
});

test('no notice when nothing was reduced, or when an older server sends no report', () => {
  assert.equal(copilotContextNotice(unreduced), null);
  assert.equal(copilotContextNotice(undefined), null);
  assert.equal(copilotContextNotice(null), null);
});

test('standard limits are reported even when no provider budget applied', () => {
  const notice = copilotContextNotice({ ...unreduced, reduced: true, reason: 'standardLimits', artifactsTruncated: 3, versionsOmitted: 4 });
  assert.equal(
    notice,
    "This answer used reduced project context (by the Copilot's standard context limits). Artifacts: 12 in full, 3 shortened. " +
      'Also left out: 4 changes from the version history. Items left out still exist - the answer may be incomplete.',
  );
});

test('a provider-budget reduction names the provider and every kind of item left out', () => {
  const notice = copilotContextNotice({
    reduced: true,
    reason: 'providerBudget',
    provider: 'Groq',
    inputBudget: 4000,
    artifactsFull: 5,
    artifactsTruncated: 2,
    artifactsTitleOnly: 7,
    artifactsOmitted: 30,
    versionsOmitted: 1,
    impactCodesOmitted: 6,
    excerptsOmitted: 3,
  });
  assert.equal(
    notice,
    'This answer used reduced project context (to fit the request limit of Groq). Artifacts: 5 in full, 2 shortened, 7 by title only, ' +
      '30 left out. Also left out: 1 change from the version history, 6 impacted artifacts, 3 document excerpts. ' +
      'Items left out still exist - the answer may be incomplete.',
  );
  assert.ok(!notice!.includes('full context'));
});

test('a user who may not read raw sources is told nothing about documents', () => {
  const notice = copilotContextNotice({ ...unreduced, reduced: true, reason: 'standardLimits', artifactsOmitted: 2, excerptsOmitted: null });
  assert.ok(notice);
  assert.ok(!/document|excerpt/i.test(notice!));
});
