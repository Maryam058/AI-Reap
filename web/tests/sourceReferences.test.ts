// Covers the "Why it matters" source links (ClarificationQuestionCard) and how SourceDocumentViewer
// finds and highlights the cited place. Which references exist is decided by the server
// (SourceReferenceIndex, tested in AiReap.Tests SourceReferenceTests).
import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { SourceReference } from '../src/api/types.ts';
import {
  alignParagraphs,
  findMatches,
  headingTitle,
  locateReference,
  markExcerpt,
  matchesBefore,
  occurrenceOf,
  originalKind,
  pageForLine,
  reasonSegments,
  referenceAriaLabel,
  sourceFormatNote,
  sourceLines,
} from '../src/lib/sourceReferences.ts';

const SOURCE_ID = '6a1b5f0e-6a77-4c1f-9b55-0d7c7e3b2a11';
const SRS = [
  'Triage Assessment Module',
  '',
  '3.2 User Roles',
  'Nurses and doctors use the module. Administrators manage access permissions for every role.',
  '',
  '4. Security',
  'Passwords are stored hashed.',
  '',
  '12.7 Decline Categories',
  'The eight decline categories are listed in the decline popup.',
].join('\r\n');

const ref = (over: Partial<SourceReference>): SourceReference => ({
  label: 'Section 3.2',
  start: 0,
  sourceId: SOURCE_ID,
  sectionNumber: '3.2',
  heading: '3.2 User Roles',
  line: 2,
  endLine: 4,
  anchor: 'L2',
  excerpt: 'Nurses and doctors use the module.',
  excerptIsQuote: false,
  ...over,
});

const links = (segments: ReturnType<typeof reasonSegments>) => segments.filter((s) => s.reference).map((s) => s.text);
const joined = (segments: ReturnType<typeof reasonSegments>) => segments.map((s) => s.text).join('');

test('a verified reference becomes a link and the surrounding text and punctuation are kept', () => {
  const reason = 'The SRS describes access permissions in Section 3.2 — User Roles. Without it, roles may be incomplete.';
  const label = 'Section 3.2 — User Roles';
  const segments = reasonSegments(reason, [ref({ label, start: reason.indexOf(label) })]);
  assert.deepEqual(links(segments), [label]);
  assert.equal(joined(segments), reason);
  assert.equal(segments[0].text, 'The SRS describes access permissions in ');
  assert.equal(segments[2].text, '. Without it, roles may be incomplete.');
});

test('several references in one reason are separate links, each opening its own place', () => {
  const reason = 'Sections 3.2 and 12.7 disagree; §4 is silent.';
  const segments = reasonSegments(reason, [
    ref({ label: '§4', start: reason.indexOf('§4'), sectionNumber: '4', heading: '4. Security', line: 5, endLine: 7 }),
    ref({ label: 'Sections 3.2', start: 0 }),
    ref({ label: '12.7', start: reason.indexOf('12.7'), sectionNumber: '12.7', heading: '12.7 Decline Categories', line: 8, endLine: 10 }),
  ]);
  assert.deepEqual(links(segments), ['Sections 3.2', '12.7', '§4']);
  assert.deepEqual(
    segments.filter((s) => s.reference).map((s) => s.reference!.line),
    [2, 8, 5],
  );
  assert.equal(joined(segments), reason);
});

test('without verified references a reason is plain text - including old questions and unresolved sections', () => {
  const reason = 'Section 9.9 is cited, see page 4.';
  for (const references of [undefined, null, [], 'nonsense' as unknown as unknown[]]) {
    const segments = reasonSegments(reason, references);
    assert.deepEqual(segments, [{ text: reason }]);
  }
  // A malformed or out-of-place reference is dropped, never linked somewhere else.
  const malformed = [
    { label: '', start: 0, sourceId: SOURCE_ID, line: 0, endLine: 1 },
    { label: 'Section 9.9', start: 0, sourceId: '', line: 0, endLine: 1 },
    { label: 'Section 9.9', start: 0, sourceId: SOURCE_ID, line: -1, endLine: 1 },
    { label: 'Section 9.9', start: 0, sourceId: SOURCE_ID, line: 3, endLine: 3 },
    ref({ label: 'Section 3.2', start: 0 }), // not in this reason
    null,
  ];
  assert.deepEqual(links(reasonSegments(reason, malformed)), []);
});

test('a label that moved takes its first free occurrence, and overlapping links are not drawn', () => {
  const reason = 'See Section 3.2 and again Section 3.2.';
  const segments = reasonSegments(reason, [ref({ start: 99 }), ref({ start: 4 }), ref({ label: 'Section 3.2 and', start: 4 })]);
  assert.deepEqual(links(segments), ['Section 3.2', 'Section 3.2']);
  assert.equal(joined(segments), reason);
});

test('markup in a reason stays text: segments are strings, and markup is never part of a link', () => {
  const reason = '<img src=x onerror=alert(1)><script>alert(2)</script> Section 3.2 is unclear.';
  const segments = reasonSegments(reason, [ref({ start: reason.indexOf('Section 3.2') })]);
  assert.deepEqual(links(segments), ['Section 3.2']);
  assert.equal(segments[0].text, '<img src=x onerror=alert(1)><script>alert(2)</script> ');
  assert.ok(segments.every((s) => typeof s.text === 'string'));
  // A label carrying markup is still only text for React to render.
  const hostile = '<b onclick=alert(1)>Section 3.2</b>';
  assert.deepEqual(links(reasonSegments(hostile, [ref({ label: hostile, start: 0 })])), [hostile]);
});

test('the viewer goes to the cited section by its heading, and finds it if the line moved', () => {
  const lines = sourceLines(SRS);
  assert.equal(lines.length, 10); // \r\n split like the server
  assert.deepEqual(locateReference(lines, ref({})), { status: 'located', line: 2, endLine: 4, headingLine: 2 });
  assert.deepEqual(locateReference(lines, ref({ heading: '12.7 Decline Categories', line: 0, endLine: 2 })), {
    status: 'located',
    line: 8,
    endLine: 10,
    headingLine: 8,
  });
});

test('a link that resolved to a contents entry opens the section itself, through its subsections', () => {
  const lines = [
    'Table of Contents',
    '4. System OverviewPAGEREF bm10 \\h9',
    '4.2 Actors and RolesPAGEREF bm12 \\h9',
    '4.2.1 Clinical Staff\t10',
    '4.3 Constraints ..... 11',
    '4. System Overview',
    '4.2 Actors and Roles',
    'Nurses triage patients.',
    '4.2.1 Clinical Staff',
    'Doctors confirm the triage level.',
    '',
    '4.3 Constraints',
    'Runs on the existing PMS.',
  ];
  const entry = ref({ label: 'Section 4.2', heading: '4.2 Actors and RolesPAGEREF bm12 \\h9', line: 2, endLine: 3, excerpt: '4.2.1 Clinical Staff 10' });
  assert.deepEqual(locateReference(lines, entry), {
    status: 'located',
    line: 6,
    endLine: 10,
    headingLine: 6,
    excerpt: 'Nurses triage patients. 4.2.1 Clinical Staff Doctors confirm the triage level.',
  });
  // An entry whose section is not in the body stays where it was.
  assert.deepEqual(locateReference(lines.slice(0, 5), entry), { status: 'located', line: 2, endLine: 3, headingLine: 2 });
  assert.equal(headingTitle('4.2 Actors and RolesPAGEREF bm12 \\h9'), '4.2 Actors and Roles');
  assert.equal(headingTitle('4.2.1 Clinical Staff\t10'), '4.2.1 Clinical Staff');
  assert.equal(headingTitle('Release Phase 2'), 'Release Phase 2');
});

test('a quote reference is located by its excerpt, with its section heading marked', () => {
  const lines = sourceLines(SRS);
  const quote = ref({ label: 'popup', line: 9, endLine: 10, heading: '12.7 Decline Categories', excerpt: 'listed in the decline popup', excerptIsQuote: true });
  assert.deepEqual(locateReference(lines, quote), { status: 'located', line: 9, endLine: 10, headingLine: 8 });
  assert.deepEqual(markExcerpt(lines[9], quote.excerpt), ['The eight decline categories are ', 'listed in the decline popup', '.']);
  assert.equal(markExcerpt(lines[9], 'not here'), null);
});

test('a reference the fetched source does not hold is reported missing, never highlighted elsewhere', () => {
  const lines = sourceLines(SRS);
  assert.deepEqual(locateReference(lines, ref({ heading: '9.9 Auditors' })), { status: 'missing' });
  assert.deepEqual(locateReference(lines, ref({ heading: null })), { status: 'missing' });
  assert.deepEqual(locateReference(lines, ref({ excerpt: 'Audit entries are kept for ten years.', excerptIsQuote: true })), { status: 'missing' });
  assert.deepEqual(locateReference([], ref({})), { status: 'missing' });
});

test('the viewer says what it can show for each upload format', () => {
  assert.match(sourceFormatNote('SRS v2.pdf'), /extracted from SRS v2\.pdf.*pages, images and layout are not shown/);
  assert.match(sourceFormatNote('brief.DOCX'), /Paragraph text extracted from brief\.DOCX.*Tables/);
  assert.equal(sourceFormatNote('notes.txt'), 'notes.txt, as uploaded.');
  assert.equal(sourceFormatNote(null), 'The requirement text as it was entered.');
});

test('a link is named by what it opens and starts with its visible words', () => {
  assert.equal(referenceAriaLabel(ref({})), 'Section 3.2: open the requirement source at "3.2 User Roles"');
});

// ---- the original document ------------------------------------------------------------------------

test('the original is shown only for a kept PDF or DOCX', () => {
  assert.equal(originalKind({ originalDocument: { format: 'pdf' } }), 'pdf');
  assert.equal(originalKind({ originalDocument: { format: 'docx' } }), 'docx');
  assert.equal(originalKind({ originalDocument: null }), null); // legacy, pasted or TXT
  assert.equal(originalKind({}), null);
  assert.equal(originalKind({ originalDocument: { format: 'exe' } }), null);
});

test('a text line is mapped to its PDF page by the page map recorded at upload', () => {
  const starts = [0, 4, 9];
  assert.equal(pageForLine(starts, 0), 1);
  assert.equal(pageForLine(starts, 3), 1);
  assert.equal(pageForLine(starts, 4), 2);
  assert.equal(pageForLine(starts, 12), 3);
  // No map, or one that cannot be right: no page, rather than a wrong one.
  assert.equal(pageForLine(null, 3), null);
  assert.equal(pageForLine([], 3), null);
  assert.equal(pageForLine([0, 9, 4], 5), null);
  assert.equal(pageForLine(starts, -1), null);
});

test('a repeated heading is told apart by which repeat it is, not by the first match', () => {
  const lines = ['1 Billing', 'Scope', 'Billing scope.', '2 Security', 'Scope', 'Security scope.'];
  assert.equal(occurrenceOf(lines, 1), 0);
  assert.equal(occurrenceOf(lines, 4), 1);
  assert.equal(occurrenceOf(lines, 4, 3), 0); // counted from the start of its page
});

test('a DOCX reference is aligned to the paragraphs whose text matches, also when the rendering has extra paragraphs', () => {
  const lines = ['1 Billing', 'Scope', 'Billing scope.', '2 Security', 'Scope', 'Security scope.', 'Other'];
  // Same paragraphs: the paragraph at the same position.
  assert.deepEqual(alignParagraphs(lines, lines, 4, 6), { first: 4, last: 5 });
  // The renderer added a paragraph (e.g. a table caption) before: found by repeat, still the second "Scope".
  const rendered = ['Cover page', ...lines];
  assert.deepEqual(alignParagraphs(rendered, lines, 4, 6), { first: 5, last: 6 });
  // The cited range stops at the first paragraph that does not match its line.
  const edited = ['1 Billing', 'Scope', 'Something else', '2 Security'];
  assert.deepEqual(alignParagraphs(edited, lines, 1, 3), { first: 1, last: 1 });
  // Not there at all: nothing is aligned.
  assert.equal(alignParagraphs(['Unrelated'], lines, 4, 6), null);
  assert.equal(alignParagraphs(lines, ['', 'x'], 0, 1), null);
});

test('text is matched across the pieces a PDF or DOCX splits it into, ignoring case, spacing and dashes', () => {
  const pieces = ['12.7 Decl', 'ine  Categ', 'ories – list'];
  const [match] = findMatches(pieces, '12.7 decline categories - list');
  assert.deepEqual(match, { start: { segment: 0, offset: 0 }, end: { segment: 2, offset: 12 } });
  // Words drawn apart, joined by a space.
  assert.equal(findMatches(['12.7', 'Decline'], '12.7 Decline').length, 0);
  assert.equal(findMatches(['12.7', 'Decline'], '12.7 Decline', ' ').length, 1);
  // Every occurrence, in order; nothing for an empty or absent query.
  assert.equal(findMatches(['Scope here. Scope there.'], 'scope').length, 2);
  assert.deepEqual(findMatches(['abc'], ''), []);
  assert.deepEqual(findMatches(['abc'], 'xyz'), []);
});

test('which match on a page is the cited line counts matches inside longer lines too', () => {
  const page = ['Scope', 'Billing scope.', 'Scope', 'Security scope.'];
  assert.equal(matchesBefore(page, 0, 0, 'Scope'), 0);
  assert.equal(matchesBefore(page, 0, 2, 'Scope'), 2); // the heading on line 2 is the third match
  assert.equal(matchesBefore(page, 2, 2, 'Scope'), 0);
});
