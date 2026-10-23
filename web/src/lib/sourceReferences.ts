// Clarification questions' links into their requirement source (ClarificationQuestionCard,
// SourceDocumentViewer). Pure, so the rules are testable: a link is drawn only from a reference the
// server resolved (SourceReferenceIndex) whose label is still at its place in the reason, and the
// viewer highlights it only where the source text it fetched still matches. Everything else - an
// unresolved "Section 9.9", a page, any markup in the reason - stays plain text; segments are
// strings rendered by React as text, never as HTML.
import type { SourceReference } from '../api/types';

export interface ReasonSegment {
  text: string;
  reference?: SourceReference;
}

const isIndex = (n: unknown): n is number => typeof n === 'number' && Number.isInteger(n) && n >= 0;

function isUsable(r: unknown): r is SourceReference {
  if (typeof r !== 'object' || r === null) return false;
  const ref = r as Partial<SourceReference>;
  return (
    typeof ref.label === 'string' &&
    ref.label.length > 0 &&
    typeof ref.sourceId === 'string' &&
    ref.sourceId.length > 0 &&
    isIndex(ref.line) &&
    isIndex(ref.endLine) &&
    ref.endLine > ref.line
  );
}

// The reason split into plain text and links, in order. A reference whose label is no longer at its
// stored start (an older record, an edited reason) takes the label's first free occurrence; one whose
// label is not in the reason, or overlaps an earlier link, is dropped.
export function reasonSegments(reason: string, references: readonly unknown[] | null | undefined): ReasonSegment[] {
  const placed: { start: number; end: number; reference: SourceReference }[] = [];
  const free = (start: number, end: number) => placed.every((p) => end <= p.start || p.end <= start);

  for (const candidate of Array.isArray(references) ? references : []) {
    if (!isUsable(candidate)) continue;
    const { label } = candidate;
    let start = isIndex(candidate.start) && reason.startsWith(label, candidate.start) ? candidate.start : -1;
    if (start < 0 || !free(start, start + label.length)) {
      start = -1;
      for (let at = reason.indexOf(label); at >= 0; at = reason.indexOf(label, at + 1)) {
        if (free(at, at + label.length)) {
          start = at;
          break;
        }
      }
    }
    if (start >= 0) placed.push({ start, end: start + label.length, reference: candidate });
  }

  placed.sort((a, b) => a.start - b.start);
  const segments: ReasonSegment[] = [];
  let at = 0;
  for (const p of placed) {
    if (p.start > at) segments.push({ text: reason.slice(at, p.start) });
    segments.push({ text: reason.slice(p.start, p.end), reference: p.reference });
    at = p.end;
  }
  if (at < reason.length || segments.length === 0) segments.push({ text: reason.slice(at) });
  return segments;
}

// The source's lines exactly as the server splits them (SourceReferenceIndex.Lines).
export const sourceLines = (text: string): string[] => text.split(/\r\n|\r|\n/);

export const normalize = (text: string) =>
  text
    .replace(/[‘’]/g, "'")
    .replace(/[“”]/g, '"')
    .replace(/[–—−]/g, '-')
    .replace(/\s+/g, ' ')
    .trim()
    .toLowerCase();

export type ReferenceLocation =
  // excerpt: the section's text, when the reference was moved off a contents entry (whose stored
  // excerpt is the contents); otherwise the reference's own excerpt applies.
  | { status: 'located'; line: number; endLine: number; headingLine: number | null; excerpt?: string }
  | { status: 'missing' };

// Where the reference is in the fetched source text. A section reference is confirmed by its heading
// line (searched for if the stored line no longer holds it); a quote reference by its excerpt. Never
// guesses: when neither is found the viewer says so instead of highlighting something else.
export function locateReference(lines: readonly string[], reference: SourceReference): ReferenceLocation {
  const span = Math.max(1, reference.endLine - reference.line);
  const clamp = (line: number) => ({ line, endLine: Math.min(lines.length, line + span) });

  if (reference.excerptIsQuote && reference.excerpt) {
    const quote = normalize(reference.excerpt);
    const holds = (line: number) => normalize(lines.slice(line, line + span).join('\n')).includes(quote);
    const line = reference.line < lines.length && holds(reference.line) ? reference.line : lines.findIndex((_, i) => holds(i));
    if (line < 0) return { status: 'missing' };
    const heading = reference.heading ? findHeading(lines, reference.heading.trim(), line, -1) : -1;
    return { status: 'located', ...clamp(line), headingLine: heading >= 0 ? heading : null };
  }

  const heading = reference.heading?.trim();
  if (!heading) return { status: 'missing' };
  const line = lines[reference.line]?.trim() === heading ? reference.line : findHeading(lines, heading, 0, 1);
  if (line < 0) return { status: 'missing' };
  const section = TOC_TAIL.test(heading) ? sectionFromContents(lines, line) : null;
  return section
    ? { status: 'located', ...section, headingLine: section.line, excerpt: sectionExcerpt(lines, section.line, section.endLine) }
    : { status: 'located', ...clamp(line), headingLine: line };
}

// A table-of-contents entry: a page number after dot leaders, a tab or a wide gap, or the field
// instruction Word contents entries carried in DOCX text extracted before those were dropped
// ("4.2 Actors and RolesPAGEREF bm12 \h9"). As SourceReferenceIndex.TableOfContentsTail.
const TOC_TAIL = /(?:(?:\s*(?:\.{3,}|…+)\s*|\s{2,}|\t+)\d+|\s*PAGEREF\s+\S+(?:\s*\\[a-z])*\s*\d*)\s*$/;
const SECTION_NUMBER = /^(\d+(?:\.\d+)*)\.?\s/;

// A heading as a reader would name it, without a contents entry's page number or field instruction.
export const headingTitle = (heading: string) => heading.trim().replace(TOC_TAIL, '').trim();

// The section's body text, as the server's SectionExcerpt: up to 300 characters, cut at a word.
function sectionExcerpt(lines: readonly string[], line: number, endLine: number): string {
  const body = lines.slice(line + 1, endLine).map((l) => l.trim()).filter(Boolean).join(' ');
  if (body.length <= 300) return body;
  const cut = body.slice(0, 300);
  const space = cut.lastIndexOf(' ');
  return `${space > 150 ? cut.slice(0, space) : cut}…`;
}

// The section a contents entry lists, in the body: its heading after the contents, running to the
// heading of the next entry that is not one of its subsections. Older links point at the entry
// itself (it was not recognised as one); null when the body heading is not in the source.
function sectionFromContents(lines: readonly string[], entry: number): { line: number; endLine: number } | null {
  const title = (i: number) => headingTitle(lines[i]);
  const line = findHeading(lines, title(entry), entry + 1, 1);
  if (line < 0) return null;

  const number = SECTION_NUMBER.exec(title(entry))?.[1];
  let endLine = lines.length;
  for (let i = entry + 1; i < line && TOC_TAIL.test(lines[i]); i++) {
    const next = SECTION_NUMBER.exec(title(i))?.[1];
    if (number && next?.startsWith(`${number}.`)) continue;
    const at = findHeading(lines, title(i), line + 1, 1);
    if (at >= 0) {
      endLine = at;
      break;
    }
  }
  while (endLine - 1 > line && !lines[endLine - 1].trim()) endLine--;
  return { line, endLine };
}

// The nearest line equal to the heading: forwards from `from`, or backwards (direction -1).
function findHeading(lines: readonly string[], heading: string, from: number, direction: 1 | -1): number {
  for (let i = Math.min(from, lines.length - 1); i >= 0 && i < lines.length; i += direction) {
    if (lines[i].trim() === heading) return i;
  }
  return -1;
}

// A line split around the excerpt, when the whole excerpt is on that line - for a <mark>.
export function markExcerpt(line: string, excerpt: string | null | undefined): [string, string, string] | null {
  if (!excerpt) return null;
  const at = line.indexOf(excerpt);
  if (at < 0) {
    const lower = line.toLowerCase().indexOf(excerpt.toLowerCase());
    if (lower < 0) return null;
    return [line.slice(0, lower), line.slice(lower, lower + excerpt.length), line.slice(lower + excerpt.length)];
  }
  return [line.slice(0, at), line.slice(at, at + excerpt.length), line.slice(at + excerpt.length)];
}

// What the viewer shows, by upload format. AI-REAP retains the text extracted at upload, not the file
// (§6, DocumentTextExtractor), so a PDF's pages and a DOCX's tables and images cannot be shown.
export function sourceFormatNote(fileName: string | null | undefined): string {
  const extension = fileName?.toLowerCase().match(/\.([a-z0-9]+)$/)?.[1];
  switch (extension) {
    case 'pdf':
      return `Text extracted from ${fileName} when it was uploaded. Only this text is kept, so the PDF’s pages, images and layout are not shown.`;
    case 'docx':
      return `Paragraph text extracted from ${fileName} when it was uploaded. Tables, headers, footers and images are not kept, so they are not shown.`;
    case 'txt':
      return `${fileName}, as uploaded.`;
    case undefined:
      return 'The requirement text as it was entered.';
    default:
      return `Text extracted from ${fileName} when it was uploaded.`;
  }
}

// ---- a question's evidence (ClarificationQuestionCard) -----------------------------------------------

// Whether an evidence passage can be opened and highlighted: it was placed in the source by the server.
export const isLocatedEvidence = (reference: unknown): reference is SourceReference => isUsable(reference);

// How a question's evidence is introduced, by what the question is about - so a question about missing
// information never reads as if the missing detail were quoted.
export function evidenceHeading(kind: string | null | undefined, count: number): string {
  switch (kind) {
    case 'missing':
      return 'Context in the source — the information asked for is not written there';
    case 'ambiguous':
      return count > 1 ? 'The ambiguous statements' : 'The ambiguous statement';
    case 'contradiction':
      return count > 1 ? 'The conflicting statements' : 'The conflicting statement';
    case 'undefined-rule':
      return 'Where the rule is referred to';
    default:
      return count > 1 ? 'The passages that raised this question' : 'The passage that raised this question';
  }
}

// What the viewer calls the highlighted passage, by the same rule.
export function evidencePurpose(kind: string | null | undefined): string {
  switch (kind) {
    case 'missing':
      return 'Context for the missing information';
    case 'ambiguous':
      return 'Ambiguous statement';
    case 'contradiction':
      return 'Conflicting statement';
    case 'undefined-rule':
      return 'Where the rule is referred to';
    default:
      return 'Quoted evidence';
  }
}

// Why a recovered passage is linked, for a question saved before its evidence was kept.
export function recoveryNote(recovery: SourceReference['recovery']): string | null {
  switch (recovery) {
    case 'generation-response':
      return 'Linked from the passage the analysis quoted when it raised this question.';
    case 'quoted-in-question':
      return 'Linked from the words this question quotes.';
    case 'requirement-id':
      return 'Linked from the requirement this question names.';
    default:
      return null;
  }
}

// Said when the quoted words are written in more than one place in the source: the server picks the
// one in a section the question cites or nearest its other evidence, else the first - so the reader
// is asked to check rather than told it is certain.
export function occurrenceNote(reference: SourceReference): string | null {
  const n = reference.occurrences;
  return typeof n === 'number' && n > 1 ? `These words appear ${n} times in the source — check that the highlighted one is the place meant.` : null;
}

// The accessible name of a link: what it opens, not only its words.
export function referenceAriaLabel(reference: SourceReference): string {
  const where = reference.heading ? `"${reference.heading}"` : `"${reference.label}"`;
  return `${reference.label}: open the requirement source at ${where}`;
}

// ---- the original document (SourceDocumentViewer: PdfOriginalView, DocxOriginalView) ----------------
// A reference is a place in the source's extracted text (RawText lines). In the original it is found
// again from that text - never from the model's section number alone - and only where the original's
// own text matches; anything that cannot be matched is reported, not guessed.

export type OriginalKind = 'pdf' | 'docx';

export function originalKind(source: { originalDocument?: { format: string } | null }): OriginalKind | null {
  const format = source.originalDocument?.format;
  return format === 'pdf' || format === 'docx' ? format : null;
}

// The 1-based page a text line is on, from the line each page starts on (recorded at upload).
// Null when the map is missing or unusable.
export function pageForLine(pageLineStarts: readonly number[] | null | undefined, line: number): number | null {
  if (!Array.isArray(pageLineStarts) || pageLineStarts.length === 0 || !isIndex(line)) return null;
  let page = 0;
  for (let i = 0; i < pageLineStarts.length; i++) {
    if (!isIndex(pageLineStarts[i]) || (i > 0 && pageLineStarts[i] < pageLineStarts[i - 1])) return null;
    if (pageLineStarts[i] <= line) page = i;
  }
  return page + 1;
}

// Which repeat of its own text a line is, counting from `from`: 0 for the first line with that text,
// 1 for the second, ... So a heading that appears twice ("Scope" in two chapters) is told apart by
// position, not by picking the first match.
export function occurrenceOf(lines: readonly string[], line: number, from = 0): number {
  const text = normalize(lines[line] ?? '');
  let n = 0;
  for (let i = Math.max(0, from); i < line; i++) if (normalize(lines[i]) === text) n++;
  return n;
}

// How many times the query occurs in lines [from, line) - which match of it, counting from the start
// of a PDF page, is the one on `line`. Counted as findMatches counts (inside longer lines too), since
// that is how the page's text layer is searched.
export function matchesBefore(lines: readonly string[], from: number, line: number, query: string): number {
  let n = 0;
  for (let i = Math.max(0, from); i < line && i < lines.length; i++) n += findMatches([lines[i]], query).length;
  return n;
}

export interface ParagraphSpan {
  first: number;
  last: number; // inclusive
}

// The texts a rendered DOCX paragraph or table row may have for one extracted line, normalized: the
// line itself, and the line without the automatic number the extractor puts before it ("3.2\tScope" -
// the viewer draws that number, it is not in the paragraph's text). A note reference "[^1]" / "[^e1]"
// is drawn as its number. Empty for a blank line.
export function docxLineKeys(line: string): string[] {
  const text = line.replace(/\[\^e?(\d+)\]/g, '$1');
  const keys = [normalize(text)];
  const unnumbered = text.match(/^[^\t]{1,24}\t(.*)$/s);
  if (unnumbered) keys.push(normalize(unnumbered[1]));
  return keys.filter((k, i) => k && keys.indexOf(k) === i);
}

// The original's paragraphs (or table rows) that hold text lines [line, endLine). DOCX text is
// extracted one line per paragraph or table row, so the block at the same position is tried first; if
// the rendering has more or fewer blocks, the same repeat of the line's text is used instead. Following
// blocks are included only while they still match their lines. Null when the line's text is not among
// them. Lines are compared as docxLineKeys reads them.
export function alignParagraphs(
  paragraphTexts: readonly string[],
  lines: readonly string[],
  line: number,
  endLine: number,
): ParagraphSpan | null {
  const want = docxLineKeys(lines[line] ?? '');
  if (!want.length) return null;
  const matches = (text: string, l: number) => docxLineKeys(lines[l] ?? '').includes(normalize(text));
  const same = (p: number, l: number) => p < paragraphTexts.length && matches(paragraphTexts[p], l);
  // Which repeat of the line's text this is, counted as same() compares.
  const nth = lines.slice(0, line).filter((l) => docxLineKeys(l).some((k) => want.includes(k))).length;

  let first = same(line, line) && occurrenceOf(paragraphTexts, line) === nth ? line : -1;
  if (first < 0) {
    for (let p = 0, seen = 0; p < paragraphTexts.length; p++) {
      if (matches(paragraphTexts[p], line) && seen++ === nth) {
        first = p;
        break;
      }
    }
  }
  if (first < 0) return null;

  let last = first;
  for (let l = line + 1, p = first + 1; l < endLine && same(p, l); l++, p++) last = p;
  return { first, last };
}

export interface TextMatch {
  start: { segment: number; offset: number };
  end: { segment: number; offset: number }; // exclusive
}

// Every place the query occurs in a run of text segments (a PDF page's text-layer pieces, a
// paragraph's text nodes), compared as normalize() compares - case, spacing, curly quotes and dashes
// ignored. Segments are joined by `joiner`: '' for pieces of one run of text, ' ' for pieces drawn
// apart. Offsets are into the original segments, so the caller can mark exactly the matched text.
export function findMatches(segments: readonly string[], query: string, joiner: '' | ' ' = ''): TextMatch[] {
  const want = normalize(query);
  if (!want) return [];

  // The normalized text, and for each of its characters the (segment, offset) it came from.
  let text = '';
  const from: { segment: number; offset: number }[] = [];
  let pendingSpace: { segment: number; offset: number } | null = null;
  const push = (ch: string, at: { segment: number; offset: number }) => {
    if (/\s/.test(ch)) {
      if (text.length > 0 && !pendingSpace) pendingSpace = at;
      return;
    }
    if (pendingSpace) {
      text += ' ';
      from.push(pendingSpace);
      pendingSpace = null;
    }
    text += normalize(ch) || ch.toLowerCase();
    from.push(at);
  };
  segments.forEach((segment, s) => {
    if (s > 0 && joiner === ' ') push(' ', { segment: s, offset: 0 });
    for (let o = 0; o < segment.length; o++) push(segment[o], { segment: s, offset: o });
  });

  const matches: TextMatch[] = [];
  for (let at = text.indexOf(want); at >= 0; at = text.indexOf(want, at + 1)) {
    const last = from[at + want.length - 1];
    matches.push({ start: from[at], end: { segment: last.segment, offset: last.offset + 1 } });
  }
  return matches;
}
