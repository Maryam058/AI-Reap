// Covers a clarification question's source link (ClarificationQuestionCard) and how SourceDocumentViewer
// finds and highlights the cited place. Which references exist is decided by the server
// (SourceReferenceIndex, tested in AiReap.Tests SourceReferenceTests).
import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { SourceReference } from '../src/api/types.ts';
import {
  alignParagraphs,
  docxLineKeys,
  evidencePurpose,
  findMatches,
  headingTitle,
  isLocatedEvidence,
  locateReference,
  markExcerpt,
  matchesBefore,
  occurrenceOf,
  originalKind,
  pageForLine,
  referenceAriaLabel,
  sourceFormatNote,
  sourceLines,
  sourceLinkAriaLabel,
  sourceLinkLabel,
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

test('a DOCX line with its automatic number, a table row or a note reference is aligned to the block the viewer draws', () => {
  // As DocumentTextExtractor writes them: the number before a tab, a row's cells joined by " | ".
  const lines = ['1	Introduction', '3.2	Leave approval', 'Leave type | Approver', 'Annual leave | Line manager', 'Refunds take five days.[^1]'];
  // As docx-preview draws them: the number is a CSS counter, not text; a note reference is its number.
  const blocks = ['Introduction', 'Leave approval', 'Leave type | Approver', 'Annual leave | Line manager', 'Refunds take five days.1'];
  assert.deepEqual(alignParagraphs(blocks, lines, 1, 2), { first: 1, last: 1 });
  assert.deepEqual(alignParagraphs(blocks, lines, 3, 5), { first: 3, last: 4 });
  // A row is not matched by one of its cells alone.
  assert.equal(alignParagraphs(['Annual leave', 'Line manager'], lines, 3, 4), null);
  // A contents entry keeps its own text as a key, so it is still found whole.
  assert.deepEqual(docxLineKeys('Actors and Roles	9'), ['actors and roles 9', '9']);
  assert.deepEqual(docxLineKeys('FR-1:	Users sign in.'), ['fr-1: users sign in.', 'users sign in.']);
  assert.deepEqual(docxLineKeys(''), []);
});

// ---- a question's evidence (ClarificationQuestionCard) ----------------------------------------------

const evidence = (over: Partial<SourceReference> = {}): SourceReference => ({
  label: 'The portal shall confirm a booking promptly.',
  start: 0,
  sourceId: SOURCE_ID,
  line: 4,
  endLine: 5,
  anchor: 'L4',
  excerpt: 'The portal shall confirm a booking promptly.',
  excerptIsQuote: true,
  ...over,
});

test('only evidence the server placed in the source can be opened', () => {
  assert.equal(isLocatedEvidence(evidence()), true);
  assert.equal(isLocatedEvidence(evidence({ line: -1, endLine: -1 })), false); // verified words, no place
  assert.equal(isLocatedEvidence(evidence({ sourceId: '' })), false);
  assert.equal(isLocatedEvidence(null), false);
});

test('the viewer names the highlighted passage by what the question is about, and missing information is never presented as quoted', () => {
  assert.equal(evidencePurpose('missing'), 'Context for the missing information');
  assert.equal(evidencePurpose('contradiction'), 'Conflicting statement');
  assert.equal(evidencePurpose(undefined), 'Quoted evidence');
});

test('a source link is named by the verified section heading, never by offsets, and none is invented', () => {
  assert.equal(sourceLinkLabel(evidence({ heading: '3.2 User Notifications' })), '3.2 User Notifications');
  // A contents entry's page number and a Markdown heading marker are not part of the name.
  assert.equal(sourceLinkLabel(evidence({ heading: '4.2 Actors and Roles ........ 9' })), '4.2 Actors and Roles');
  assert.equal(sourceLinkLabel(evidence({ heading: '## Booking' })), 'Booking');
  // No heading: a neutral label - no section number, line or offset is made up.
  for (const heading of [null, undefined, '']) {
    const label = sourceLinkLabel(evidence({ heading, sectionNumber: null }));
    assert.equal(label, 'Source passage');
    assert.doesNotMatch(label, /line|§|\d/);
  }
  assert.equal(
    sourceLinkAriaLabel(evidence({ heading: '3.2 User Notifications' })),
    'Open “3.2 User Notifications” in the requirement source at the highlighted passage',
  );
  assert.equal(
    sourceLinkAriaLabel(evidence({ heading: null }), 'Statement 2'),
    'Statement 2: Open the requirement source at the highlighted passage',
  );
});
