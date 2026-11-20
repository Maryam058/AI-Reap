// Run with `npm test`. The AI Usage page's figures come from GET /api/ai/usage (AiUsageMeter, tested in
// AiReap.Tests); these cover how the page reads them.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { AiUsageSnapshot } from '../src/api/types.ts';
import { formatTokens, isEmptyUsage, modelsByTokens } from '../src/lib/aiUsage.ts';

const totals = (requests: number, input: number, output: number) => ({
  requests, inputTokens: input, outputTokens: output, cacheCreationInputTokens: 0, cacheReadInputTokens: 0,
});

const snapshot = (byModel: AiUsageSnapshot['byModel']): AiUsageSnapshot => ({
  since: '2026-11-06T13:44:05Z',
  totals: totals(byModel.reduce((n, m) => n + m.totals.requests, 0), 0, 0),
  byModel,
  recent: [],
});

test('a snapshot with no calls is empty (fresh start, or the development stub)', () => {
  assert.equal(isEmptyUsage(snapshot([])), true);
  assert.equal(isEmptyUsage(snapshot([{ provider: 'Anthropic', model: 'claude-sonnet-5-5', totals: totals(1, 10, 5) }])), false);
});

test('models are listed with the most tokens first', () => {
  const s = snapshot([
    { provider: 'Anthropic', model: 'claude-haiku-5-5', totals: totals(3, 100, 50) },
    { provider: 'Anthropic', model: 'claude-sonnet-5-5', totals: totals(1, 900, 400) },
  ]);
  assert.deepEqual(modelsByTokens(s).map((m) => m.model), ['claude-sonnet-5-5', 'claude-haiku-5-5']);
});

test('token counts are whole numbers with thousands separators', () => {
  assert.equal(formatTokens(1234567), '1,234,567');
  assert.equal(formatTokens(0), '0');
});

test('a call time includes the time of day, not only the date', async () => {
  const { formatCallTime } = await import('../src/lib/aiUsage.ts');
  const shown = formatCallTime('2026-11-06T13:41:07Z', 'en-US');
  assert.match(shown, /2026/);
  assert.match(shown, /:41:07/);
  assert.equal(formatCallTime('not a date'), '');
});
