// What the source viewer marks in yellow for a clarification question's passage (highlightTarget,
// lineHighlights, relatedPassages): the same exact text in the PDF, Word and extracted-text views, never
// a guess, and only passages of the source being shown.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { SourceReference } from '../src/api/types.ts';
import { highlightTarget, lineHighlights, locateReference, relatedPassages } from '../src/lib/sourceReferences.ts';

const SOURCE = 's1';
const LINES = [
  'Triage Assessment Module',
  '1 Billing',
  'Invoices are sent monthly. Late invoices are flagged.',
  '2 Security',
  'Passwords are stored hashed',
  'and rotated every “90 days”.',
  '12.7 Decline Categories',
  'The eight decline categories are listed in the decline popup.',
];

const ref = (over: Partial<SourceReference> = {}): SourceReference => ({
  label: 'Section 12.7',
  start: 0,
  sourceId: SOURCE,
  sectionNumber: '12.7',
  heading: '12.7 Decline Categories',
  line: 6,
  endLine: 8,
  anchor: 'L6',
  excerpt: null,
  excerptIsQuote: false,
  ...over,
});
const quote = (line: number, endLine: number, excerpt: string, over: Partial<SourceReference> = {}) =>
  ref({ label: excerpt, line, endLine, excerpt, excerptIsQuote: true, heading: null, sectionNumber: null, ...over });
const target = (r: SourceReference) => highlightTarget(LINES, r, locateReference(LINES, r));

test('a quote is highlighted by its own words, a section by its heading', () => {
  assert.deepEqual(target(quote(2, 3, 'Late invoices are flagged.')), { kind: 'quote', line: 2, endLine: 3, text: 'Late invoices are flagged.' });
  assert.deepEqual(target(ref()), { kind: 'heading', line: 6, endLine: 7, text: '12.7 Decline Categories' });
});

test('nothing is highlighted for a passage that is not located, or a quote with no words', () => {
  assert.equal(target(ref({ heading: '9.9 Auditors', line: 1, endLine: 2 })), null);
  assert.equal(target(quote(2, 3, 'Invoices are sent weekly.')), null);
  assert.equal(highlightTarget(LINES, quote(2, 3, '   '), { status: 'located', line: 2, endLine: 3, headingLine: null }), null);
  assert.equal(highlightTarget(LINES, ref(), { status: 'missing' }), null);
});

test('lineHighlights marks exactly the quoted words of a line, not the line', () => {
  const spans = lineHighlights(LINES, target(quote(2, 3, 'Late invoices are flagged.'))!);
  assert.deepEqual([...spans], [[2, [27, 53]]]);
  assert.equal(LINES[2].slice(27, 53), 'Late invoices are flagged.');
});

test('lineHighlights follows a quote over two lines, ignoring case, spacing and curly quotes', () => {
  const t = target(quote(4, 6, 'stored HASHED and   rotated every "90 days"'))!;
  const spans = lineHighlights(LINES, t);
  assert.deepEqual([...spans.keys()], [4, 5]);
  const [from4, to4] = spans.get(4)!;
  const [from5, to5] = spans.get(5)!;
  assert.equal(LINES[4].slice(from4, to4), 'stored hashed');
  assert.equal(LINES[5].slice(from5, to5), 'and rotated every “90 days”');
});

test('lineHighlights marks nothing when the words are not on the target lines', () => {
  assert.equal(lineHighlights(LINES, { kind: 'quote', line: 0, endLine: 2, text: 'Late invoices' }).size, 0);
});

test('related passages: the other passages of the same source, located, each once, never the shown one', () => {
  const shown = quote(2, 3, 'Invoices are sent monthly.');
  const passwords = quote(4, 5, 'Passwords are stored hashed');
  const section = ref();
  const elsewhere = { ...passwords, sourceId: 'another-source' };
  const missing = quote(2, 3, 'Not in this document');
  const sameAsShown = { ...shown, label: 'Statement 1 again' };
  const related = relatedPassages(LINES, shown, [shown, passwords, elsewhere, missing, sameAsShown, section, { ...section }]);
  assert.deepEqual(
    related.map((r) => [r.target.kind, r.target.line, r.target.text]),
    [
      ['quote', 4, 'Passwords are stored hashed'],
      ['heading', 6, '12.7 Decline Categories'],
    ],
  );
});
