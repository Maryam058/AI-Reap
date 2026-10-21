// Run with `npm test`. Covers the wording of the dry-run AI plan. The plan itself (part counts,
// limits, cool-downs) is computed by the backend and tested in AiReap.Tests (AiPlanTests); the
// fixtures below stand in for its answers.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { AiPlanProvider, AiPlanResult } from '../src/api/types.ts';
import {
  approxTokens,
  attentionReasons,
  partsText,
  planButtonHint,
  planNeedsAttention,
  planTone,
  providerLine,
} from '../src/lib/aiPlan.ts';

function provider(overrides: Partial<AiPlanProvider>): AiPlanProvider {
  return {
    role: 'Primary',
    provider: 'Gemini',
    model: 'gemini-3.8-flash',
    status: 'WithinLimits',
    sizeStatus: 'WithinLimits',
    availability: 'QuotaUnknown',
    cooldownUntil: null,
    wholeRequest: true,
    parts: { min: 1, max: 1, exact: true },
    estimatedRequestTokens: 85895,
    inputBudget: null,
    maxRequestTokens: null,
    maxPartSourceTokens: null,
    reservedTokensPerPart: null,
    clarificationMode: null,
    maxParts: 0,
    maxPartsSetting: null,
    estimatedTotalTokensUpperBound: 85895,
    maxOutputTokens: 65536,
    timeoutSeconds: 300,
    reasons: [{ code: 'QuotaUnknown', severity: 'Info', message: "Gemini's remaining quota is not known." }],
    ...overrides,
  };
}

const groqBlocked = provider({
  role: 'Fallback',
  provider: 'Groq',
  model: 'openai/gpt-oss-120b',
  status: 'Blocked',
  sizeStatus: 'Blocked',
  wholeRequest: false,
  parts: { min: 68, max: 68, exact: true },
  inputBudget: 4000,
  maxRequestTokens: 8000,
  maxParts: 8,
  maxPartsSetting: 'Ai:Groq:MaxChunkedParts',
  reasons: [
    { code: 'TooManyParts', severity: 'Blocking', message: 'Groq would need 68 parts; at most 8 are allowed.' },
    { code: 'QuotaUnknown', severity: 'Info', message: "Groq's remaining quota is not known." },
  ],
});

function plan(overrides: Partial<AiPlanResult>): AiPlanResult {
  return {
    operation: 'generate',
    requirementSourceId: 's1',
    generatedAt: '2026-10-15T09:00:00Z',
    context: {
      sourceTokens: 83938,
      systemPromptTokens: 592,
      fixedContextTokens: { min: 20, max: 20, exact: true },
      clarificationItems: 40,
      clarificationTokens: 900,
      businessObjectives: 1,
      documentExcerptCandidates: 0,
    },
    route: { primary: 'Gemini', fallback: 'Groq', primaryCooldownUntil: null, expectedFirst: 'Gemini', configurationError: null },
    providers: [provider({}), groqBlocked],
    overall: { status: 'Warning', summary: 'Gemini is tried first; if it is out of quota, Groq cannot take it.' },
    caveats: ['No AI provider was contacted to make this plan.'],
    ...overrides,
  };
}

test('a configured provider is never described as available: quota stays unknown', () => {
  const line = providerLine(provider({}));
  assert.equal(line, 'Gemini (primary): one whole request (~86k tokens) · quota unknown · Within size limits');
  assert.doesNotMatch(line, /available/i);
});

test('the groq part limit is shown with the part count and the limit', () => {
  assert.equal(providerLine(groqBlocked), 'Groq (fallback): 68 parts, limit 8 · quota unknown · Blocked');
});

test('a part range is shown when document excerpts make the count uncertain', () => {
  assert.equal(partsText(provider({ wholeRequest: false, parts: { min: 66, max: 68, exact: false } })), '66–68 parts');
  assert.equal(partsText(provider({ wholeRequest: false, parts: null })), 'cannot be split');
});

test('a cooling-down primary shows when it is skipped until', () => {
  const line = providerLine(provider({ availability: 'CoolingDown', cooldownUntil: '2026-10-15T10:20:00Z', status: 'Blocked' }));
  assert.match(line, /skipped until /);
  assert.match(line, /Blocked$/);
});

test('blocked, uncertain and warning plans need attention and give the button a hint; within limits does not', () => {
  const blocked = plan({ overall: { status: 'Blocked', summary: 'Gemini is skipped; Groq would need 68 parts.' } });
  assert.equal(planTone('Blocked'), 'block');
  assert.equal(planTone('Uncertain'), 'block');
  assert.equal(planTone('Warning'), 'warn');
  assert.equal(planTone('WithinLimits'), 'ok');
  assert.ok(planNeedsAttention(blocked));
  assert.match(planButtonHint(blocked)!, /expected to be refused before anything is sent or saved/);
  assert.match(planButtonHint(blocked)!, /no AI provider was contacted/);
  assert.match(planButtonHint(plan({ overall: { status: 'Uncertain', summary: 'x' } }))!, /may be refused/);

  const fine = plan({ overall: { status: 'WithinLimits', summary: 'ok' } });
  assert.equal(planNeedsAttention(fine), false);
  assert.equal(planButtonHint(fine), undefined);
  assert.equal(planButtonHint(null), undefined);
});

test('details list blocking and warning reasons once, without the info notes', () => {
  const reasons = attentionReasons(plan({}));
  assert.deepEqual(reasons, [{ provider: 'Groq', severity: 'Blocking', message: 'Groq would need 68 parts; at most 8 are allowed.' }]);
});

test('token estimates are rounded, not presented as exact counts', () => {
  assert.equal(approxTokens(85895), '~86k');
  assert.equal(approxTokens(940), '~940');
});
