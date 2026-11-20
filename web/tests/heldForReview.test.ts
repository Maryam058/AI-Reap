// Held-for-review items (lib/heldForReview): the API's heldForReview list reshaped for display, read-only.
// Which items are held is decided by the server (ClarificationFinalReviewTests in AiReap.Tests).
import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { AnalyzeRequirementResult, HeldClarification } from '../src/api/types.ts';
import { heldForReviewItems } from '../src/lib/heldForReview.ts';

const SOURCE = 'src-1';
const base: AnalyzeRequirementResult = { actors: [], capabilities: [], dataElements: [], notes: [], clarificationQuestions: [] };

const held = (over: Partial<HeldClarification> = {}): HeldClarification => ({
  topic: 'Claim approval',
  question: 'Which manager approves claims once the approval step is turned off?',
  kind: 'missing',
  evidence: [
    { label: 'q', start: 0, sourceId: SOURCE, heading: '3.2 Claim Approval', line: 10, endLine: 11, anchor: 'L10', excerpt: 'Claims over £500 are approved by a manager.', excerptIsQuote: true },
  ],
  denialLine: 14,
  denialText: 'The approval step cannot be turned off.',
  location: 'line 15',
  note: 'Not asked as written: it assumes something the source denies at line 15.',
  ...over,
});

test('an analysis without heldForReview (older records) or with null gives an empty list', () => {
  assert.deepEqual(heldForReviewItems(base, SOURCE), []);
  assert.deepEqual(heldForReviewItems({ ...base, heldForReview: null }, SOURCE), []);
  assert.deepEqual(heldForReviewItems(null, SOURCE), []);
  assert.deepEqual(heldForReviewItems({ ...base, heldForReview: 'oops' as unknown as HeldClarification[] }, SOURCE), []);
});

test('one held item keeps the question, both passages and the reason verbatim', () => {
  const [item, ...rest] = heldForReviewItems({ ...base, heldForReview: [held()] }, SOURCE);
  assert.equal(rest.length, 0);
  assert.equal(item.question, held().question);
  assert.equal(item.reason, held().note);
  assert.equal(item.kindLabel, 'Missing information');
  assert.equal(item.evidence.length, 1);
  assert.equal(item.evidence[0].text, 'Claims over £500 are approved by a manager.');
  assert.equal(item.evidence[0].where, '3.2 Claim Approval');
  assert.equal(item.evidence[0].reference?.line, 10);
  assert.equal(item.contradicting.text, 'The approval step cannot be turned off.');
  assert.equal(item.contradicting.where, 'Line 15');
  assert.deepEqual(
    { line: item.contradicting.reference?.line, endLine: item.contradicting.reference?.endLine, quote: item.contradicting.reference?.excerptIsQuote, source: item.contradicting.reference?.sourceId },
    { line: 14, endLine: 15, quote: true, source: SOURCE },
  );
});

test('multiple held items stay independent, in order', () => {
  const items = heldForReviewItems({ ...base, heldForReview: [held(), held({ question: 'Second?', denialLine: 30, denialText: 'Other line.', location: '§4.1' })] }, SOURCE);
  assert.deepEqual(items.map((i) => i.question), [held().question, 'Second?']);
  assert.deepEqual(items.map((i) => i.contradicting.where), ['Line 15', '§4.1']);
  assert.notEqual(items[0].key, items[1].key);
});

test('unlocated evidence is shown as text without a link; a passage outside a section is named by its line', () => {
  const [item] = heldForReviewItems(
    {
      ...base,
      heldForReview: [
        held({
          evidence: [
            { label: 'q', start: 0, sourceId: SOURCE, line: -1, endLine: -1, anchor: '', excerpt: 'Quoted but not found.', excerptIsQuote: true },
            { label: 'q', start: 0, sourceId: SOURCE, heading: null, line: 4, endLine: 5, anchor: 'L4', excerpt: 'Preamble line.', excerptIsQuote: true },
          ],
        }),
      ],
    },
    SOURCE,
  );
  assert.equal(item.evidence[0].reference, null);
  assert.equal(item.evidence[0].text, 'Quoted but not found.');
  assert.equal(item.evidence[0].where, 'Not located in the source');
  assert.equal(item.evidence[1].where, 'Line 5');
});

test('entries without a question are skipped; missing optional fields do not throw', () => {
  const partial = { question: 'Only a question?' } as unknown as HeldClarification;
  const items = heldForReviewItems({ ...base, heldForReview: [{ ...held(), question: '  ' }, null as unknown as HeldClarification, partial] }, SOURCE);
  assert.equal(items.length, 1);
  assert.equal(items[0].question, 'Only a question?');
  assert.deepEqual(items[0].evidence, []);
  assert.equal(items[0].contradicting.text, null);
  assert.equal(items[0].contradicting.reference, null);
  assert.equal(items[0].kindLabel, null);
  assert.ok(items[0].reason.length > 0);
});
