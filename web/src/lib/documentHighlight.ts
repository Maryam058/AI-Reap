// DOM helpers for the original-document views (SourceDocumentViewer). Kept apart from the React
// components so they can be tested on a plain DOM (tests/dom). Everything here works on nodes that are
// already in a document: text is only ever moved into <mark> elements, never parsed as HTML.
import { findMatches, type TextMatch } from './sourceReferences';

// Elements that can run code, load something or navigate, which a rendered document never needs.
const REMOVED = 'script, iframe, frame, frameset, object, embed, applet, link, meta, base, form, input, button, textarea, select, noscript, template, portal';
// URL-bearing attributes and the values allowed in them: in-document anchors, and images the renderer
// made itself from the file (blob:, data:image/). Anything else - javascript:, http(s):, file: - goes.
const URL_ATTRIBUTES = ['href', 'xlink:href', 'src', 'srcset', 'action', 'formaction', 'poster', 'background', 'ping'];
const SAFE_URL = /^(#|blob:|data:image\/(png|jpe?g|gif|bmp|webp);)/i;

// Makes rendered document markup inert before it is shown: no scripts or embedded frames, no event
// handlers, no links out of the document, no external resources. (The DOCX view also renders inside a
// sandboxed iframe without scripts; this is the second line, not the only one.)
export function sanitizeRenderedDocument(root: ParentNode): void {
  root.querySelectorAll(REMOVED).forEach((el) => el.remove());
  const all = root.querySelectorAll('*');
  all.forEach((el) => {
    for (const attr of Array.from(el.attributes)) {
      const name = attr.name.toLowerCase();
      if (name.startsWith('on')) el.removeAttribute(attr.name);
      else if (URL_ATTRIBUTES.includes(name) && !SAFE_URL.test(attr.value.trim())) el.removeAttribute(attr.name);
      else if (name === 'style' && /url\s*\(|expression\s*\(|@import/i.test(attr.value)) el.removeAttribute(attr.name);
    }
    if (el.tagName.toLowerCase() === 'a') {
      el.removeAttribute('target');
      if (!el.getAttribute('href')?.startsWith('#')) el.removeAttribute('href');
    }
  });
  // <style> from the renderer is kept (it lays the document out); external loads inside it are not.
  root.querySelectorAll('style').forEach((style) => {
    if (/@import|url\s*\(\s*['"]?\s*(https?:|\/\/|javascript:|file:)/i.test(style.textContent ?? '')) style.remove();
  });
}

function textNodes(root: Node): Text[] {
  const doc = root.ownerDocument ?? (root as Document);
  const walker = doc.createTreeWalker(root, 0x4 /* NodeFilter.SHOW_TEXT */);
  const nodes: Text[] = [];
  for (let n = walker.nextNode(); n; n = walker.nextNode()) nodes.push(n as Text);
  return nodes;
}

// The query's matches in an element's text, across its text nodes (formatting runs split a heading
// into several). `joiner` as in findMatches.
export function matchesIn(root: Node, query: string, joiner: '' | ' ' = ''): { nodes: Text[]; matches: TextMatch[] } {
  const nodes = textNodes(root);
  return { nodes, matches: findMatches(nodes.map((n) => n.data), query, joiner) };
}

// Wraps one match in <mark> elements (one per text node it covers) and returns them in order. A null
// segment stands for a gap between blocks (markAcross) and has nothing to wrap.
export function markMatch(nodes: (Text | null)[], match: TextMatch, className = 'src-hit'): HTMLElement[] {
  const marks: HTMLElement[] = [];
  // Last node first, so splitting a node does not move the offsets of the ones still to do.
  for (let s = match.end.segment; s >= match.start.segment; s--) {
    const node = nodes[s];
    if (!node) continue;
    const from = s === match.start.segment ? match.start.offset : 0;
    const to = s === match.end.segment ? match.end.offset : node.data.length;
    if (to <= from) continue;
    const middle = from > 0 ? node.splitText(from) : node;
    if (to - from < middle.data.length) middle.splitText(to - from);
    const mark = node.ownerDocument.createElement('mark');
    mark.className = className;
    middle.parentNode!.insertBefore(mark, middle);
    mark.appendChild(middle);
    marks.unshift(mark);
  }
  return marks;
}

// Marks the nth (0-based) occurrence of the query in the element, trying the pieces as one run of text
// and then as words drawn apart. Returns the marks, or [] when there is no such occurrence.
export function markOccurrence(root: Node, query: string, nth = 0, className = 'src-hit'): HTMLElement[] {
  for (const joiner of ['', ' '] as const) {
    const { nodes, matches } = matchesIn(root, query, joiner);
    if (matches.length > nth) return markMatch(nodes, matches[nth], className);
  }
  return [];
}

// Marks the first occurrence of the query in a run of consecutive blocks (a Word quote's paragraphs):
// within one block if it is there, else across them, with the break between two blocks read as a space
// and the formatting runs inside a block read as one run of text. Only the matched words are marked,
// never the rest of the blocks. Returns [] when the words are not there.
export function markAcross(blocks: readonly Node[], query: string, className = 'src-hit'): HTMLElement[] {
  for (const block of blocks) {
    const marks = markOccurrence(block, query, 0, className);
    if (marks.length) return marks;
  }
  if (blocks.length < 2) return [];
  const nodes: (Text | null)[] = [];
  blocks.forEach((block, i) => {
    if (i > 0) nodes.push(null);
    nodes.push(...textNodes(block));
  });
  const [match] = findMatches(nodes.map((n) => n?.data ?? ' '), query);
  return match ? markMatch(nodes, match, className) : [];
}

// Undoes markMatch in an element: every mark of the viewer's (the yellow highlight and the heading
// underline) is replaced by the text it held, and the split text nodes are joined again, so the next
// passage is matched on the document's own text. Marks the document itself contains are left alone.
export function unmarkAll(root: ParentNode, selector = 'mark.src-hit, mark.src-heading-mark'): void {
  const parents = new Set<Node>();
  // Innermost first: a mark inside another (two passages over the same words) goes before its outer one.
  for (const mark of Array.from(root.querySelectorAll(selector)).reverse()) {
    const parent = mark.parentNode;
    if (!parent) continue;
    while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
    mark.remove();
    parents.add(parent);
  }
  parents.forEach((p) => p.normalize());
}
