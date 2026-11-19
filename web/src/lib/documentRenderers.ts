// The two document renderers behind SourceDocumentViewer, loaded only when a viewer opens (they are
// large) and kept behind this small surface so the views can be tested without them.
//   PDF  - pdf.js: each page drawn to a canvas, with its text layer over it (for highlighting). No
//          annotation layer, so a PDF's links, forms and scripts are never active; eval is off.
//   DOCX - docx-preview: the document's own structure (paragraphs, tables, styles) as HTML, then made
//          inert (sanitizeRenderedDocument) before it is shown in a sandboxed iframe.
import { sanitizeRenderedDocument } from './documentHighlight';

export interface PdfHandle {
  pageCount: number;
  // The page's unscaled size, for laying out pages before they are drawn.
  pageSize(pageNumber: number): Promise<{ width: number; height: number }>;
  // Draws the page into the container at the scale and returns its text layer element.
  renderPage(pageNumber: number, container: HTMLElement, scale: number): Promise<HTMLElement>;
  destroy(): void;
}

export async function openPdf(data: ArrayBuffer): Promise<PdfHandle> {
  const pdfjs = await import('pdfjs-dist');
  const worker = await import('pdfjs-dist/build/pdf.worker.min.mjs?url');
  pdfjs.GlobalWorkerOptions.workerSrc = worker.default;

  const task = pdfjs.getDocument({
    data: new Uint8Array(data.slice(0)), // pdf.js takes ownership of the buffer it is given
    // pdf.js 6 compiles no code from PDF content (the old isEvalSupported switch is gone with it).
    enableXfa: false,
    disableAutoFetch: true,
    disableStream: true,
  });
  const doc = await task.promise;

  return {
    pageCount: doc.numPages,
    async pageSize(pageNumber) {
      const viewport = (await doc.getPage(pageNumber)).getViewport({ scale: 1 });
      return { width: viewport.width, height: viewport.height };
    },
    async renderPage(pageNumber, container, scale) {
      const page = await doc.getPage(pageNumber);
      const viewport = page.getViewport({ scale });
      const ratio = window.devicePixelRatio || 1;

      container.replaceChildren();
      container.style.setProperty('--total-scale-factor', String(scale));
      container.style.setProperty('--scale-round-x', '1px');
      container.style.setProperty('--scale-round-y', '1px');
      container.style.width = `${Math.floor(viewport.width)}px`;
      container.style.height = `${Math.floor(viewport.height)}px`;

      const canvas = document.createElement('canvas');
      canvas.width = Math.floor(viewport.width * ratio);
      canvas.height = Math.floor(viewport.height * ratio);
      canvas.style.width = `${Math.floor(viewport.width)}px`;
      canvas.style.height = `${Math.floor(viewport.height)}px`;
      canvas.setAttribute('aria-hidden', 'true');
      container.appendChild(canvas);
      await page.render({ canvas, viewport, transform: ratio !== 1 ? [ratio, 0, 0, ratio, 0, 0] : undefined }).promise;

      const textLayer = document.createElement('div');
      textLayer.className = 'textLayer';
      container.appendChild(textLayer);
      await new pdfjs.TextLayer({ textContentSource: page.streamTextContent(), container: textLayer, viewport }).render();
      return textLayer;
    },
    destroy() {
      void task.destroy();
    },
  };
}

// A rendered element that one line of the extracted text came from, with its text read as the
// extractor reads it.
export interface DocxBlock {
  element: HTMLElement;
  text: string;
}

export interface RenderedDocx {
  // The body as the text is extracted now: each paragraph outside a table, and each row of a table
  // (cells joined by " | "), in document order.
  blocks: DocxBlock[];
  // Every body paragraph, table cells included, one each - as DOCX text was extracted before table
  // rows were kept together, for sources stored then.
  paragraphs: DocxBlock[];
}

// An element's text with line breaks (<br>) read as spaces, as the extractor reads them.
function renderedText(el: Element): string {
  let text = '';
  const walker = el.ownerDocument.createTreeWalker(el, 0x1 | 0x4 /* SHOW_ELEMENT | SHOW_TEXT */);
  for (let n = walker.nextNode(); n; n = walker.nextNode()) {
    if (n.nodeType === 3) text += (n as Text).data;
    else if ((n as Element).tagName.toLowerCase() === 'br') text += ' ';
  }
  return text;
}

// The blocks of a rendered document body (see RenderedDocx). Headers, footers and notes (in <li>) are
// left out: their text comes after the body in the extracted text and is not aligned here.
export function docxBlocks(body: ParentNode): RenderedDocx {
  const inCell = (el: Element) => el.parentElement?.closest('td, th') != null;
  const paragraphs = Array.from(body.querySelectorAll<HTMLElement>('section article p')).filter((p) => !p.closest('li'));
  const blocks: DocxBlock[] = [];
  for (const el of Array.from(body.querySelectorAll<HTMLElement>('section article p, section article tr'))) {
    if (el.closest('li')) continue;
    if (el.tagName.toLowerCase() === 'p') {
      if (!el.closest('td, th')) blocks.push({ element: el, text: renderedText(el) });
    } else if (!inCell(el)) {
      const cells = Array.from(el.children).filter((c) => /^t[dh]$/i.test(c.tagName));
      // A cell's paragraphs joined by spaces (a table inside it too), as the extractor joins them.
      const cellText = (c: Element) => {
        const ps = Array.from(c.querySelectorAll('p'));
        return ps.length ? ps.map(renderedText).join(' ') : renderedText(c);
      };
      const text = cells.map((c) => cellText(c).replace(/\s+/g, ' ').trim().replace(/\|/g, '/')).join(' | ');
      blocks.push({ element: el, text: /[^|\s]/.test(text) ? text : '' });
    }
  }
  return { blocks, paragraphs: paragraphs.map((p) => ({ element: p, text: renderedText(p) })) };
}

// Renders the DOCX into `body` and its styles into `styles` (both in the viewer's iframe document),
// made inert first, and returns its blocks (docxBlocks).
export async function renderDocx(data: ArrayBuffer, body: HTMLElement, styles: HTMLElement): Promise<RenderedDocx> {
  const { renderAsync } = await import('docx-preview');
  // Rendered off-screen first, so nothing it produced is live until it has been made inert.
  const stagingBody = document.createElement('div');
  const stagingStyles = document.createElement('div');
  await renderAsync(data, stagingBody, stagingStyles, {
    inWrapper: true,
    breakPages: true,
    ignoreLastRenderedPageBreak: true,
    renderHeaders: true,
    renderFooters: true,
    renderFootnotes: true,
    renderEndnotes: true,
    renderComments: false,
    renderChanges: false,
    renderAltChunks: false, // embedded HTML (altChunk) would be rendered as an iframe
    useBase64URL: false,
    experimental: false,
  });
  sanitizeRenderedDocument(stagingBody);
  sanitizeRenderedDocument(stagingStyles);

  styles.replaceChildren(...Array.from(stagingStyles.childNodes).map((n) => styles.ownerDocument.adoptNode(n)));
  body.replaceChildren(...Array.from(stagingBody.childNodes).map((n) => body.ownerDocument.adoptNode(n)));
  return docxBlocks(body);
}

// ---- Word pages in a narrow pane (DocxOriginalView) ------------------------------------------------
// docx-preview lays every page out at its Word size (an A4 page is about 794px), which a side-by-side
// panel or a phone cannot show: the page is cut off. When the pane is narrower than the page, the page
// reflows instead: the frame CSS (html.src-docx-reflow) drops the fixed page width, margins and height,
// and fitDocxToWidth turns what docx-preview fixed in pt into proportions of the pane - table and column
// widths into percentages, list indents scaled down, and an image box that is still too wide zoomed to
// fit (its crop is drawn inside the box, so only the box as a whole can be scaled). Text keeps its size,
// and nothing is removed or rewritten, so the paragraphs the highlight matches are the same elements.
// Measured once from the page as rendered, before any change, so it can be fitted again on resize.

const REFLOW_CLASS = 'src-docx-reflow';
const WIDE_TABLE_CLASS = 'src-docx-wide';
// The frame's own padding around the page (DOCX_FRAME_STYLE: .docx-wrapper padding 16px each side).
export const DOCX_PAGE_GUTTER = 32;
// List indents are scaled with the text width, but never below this share of their Word size.
const MIN_INDENT_SCALE = 0.35;

interface WidthOf<T extends HTMLElement = HTMLElement> {
  el: T;
  inline: string; // the element's own style value, restored when the page fits again
}

export interface DocxLayout {
  page: number; // the widest page, px, margins included
  text: number; // that page's text width (page less its margins), px
  indents: (WidthOf & { margin: number; hang: number })[];
  tables: (WidthOf<HTMLTableElement> & { percent: number; parts: (WidthOf & { percent: number })[] })[];
  images: (WidthOf & { width: number })[];
  reflowed: boolean; // whether fitDocxToWidth last reflowed the page (nothing to restore otherwise)
}

const px = (value: string) => parseFloat(value) || 0;

export function measureDocxLayout(body: HTMLElement): DocxLayout | null {
  const view = body.ownerDocument.defaultView;
  const sections = Array.from(body.querySelectorAll<HTMLElement>('.docx-wrapper > section'));
  if (!view || sections.length === 0) return null;
  const style = (el: Element) => view.getComputedStyle(el);

  const widest = sections.reduce((a, b) => (b.offsetWidth > a.offsetWidth ? b : a));
  const page = widest.offsetWidth;
  const text = page - px(style(widest).paddingLeft) - px(style(widest).paddingRight);
  if (page <= 0 || text <= 0) return null;

  const indents: DocxLayout['indents'] = [];
  for (const p of Array.from(body.querySelectorAll<HTMLElement>('.docx-wrapper p'))) {
    const s = style(p);
    const margin = px(s.marginLeft);
    if (s.direction !== 'rtl' && margin > 0) indents.push({ el: p, inline: p.style.marginLeft, margin, hang: Math.max(0, -px(s.textIndent)) });
  }

  const tables: DocxLayout['tables'] = [];
  for (const table of Array.from(body.querySelectorAll<HTMLTableElement>('.docx-wrapper table'))) {
    const width = table.offsetWidth;
    if (width <= 0) continue;
    const cell = table.parentElement?.closest<HTMLElement>('td, th');
    const room = cell ? cell.clientWidth - px(style(cell).paddingLeft) - px(style(cell).paddingRight) : text;
    const parts: DocxLayout['tables'][number]['parts'] = [];
    const cols = Array.from(table.querySelectorAll<HTMLElement>(':scope > colgroup > col'));
    const cells = Array.from(table.rows).flatMap((row) => Array.from(row.cells));
    for (const el of cols) parts.push({ el, inline: el.style.width, percent: (px(style(el).width) / width) * 100 });
    for (const el of cells) if (el.style.width) parts.push({ el, inline: el.style.width, percent: (el.offsetWidth / width) * 100 });
    tables.push({ el: table, inline: table.style.width, percent: Math.min(100, (width / Math.max(room, 1)) * 100), parts });
  }

  const images: DocxLayout['images'] = [];
  for (const img of Array.from(body.querySelectorAll<HTMLElement>('.docx-wrapper img, .docx-wrapper svg'))) {
    if (img.parentElement?.closest('svg')) continue;
    // docx-preview draws a cropped picture inside a box of the picture's size: scale the box.
    const holder = img.parentElement;
    const box = holder && holder.tagName === 'DIV' && holder.style.width ? holder : img;
    const width = box.getBoundingClientRect().width;
    if (width > 0 && !images.some((i) => i.el === box)) images.push({ el: box, inline: box.style.getPropertyValue('zoom'), width });
  }

  return { page, text, indents, tables, images, reflowed: false };
}

// Reflows the page when the pane (width, px) is narrower than it, or restores it; true when reflowed.
export function fitDocxToWidth(layout: DocxLayout, root: HTMLElement, width: number): boolean {
  const reflow = width > 0 && width < layout.page + DOCX_PAGE_GUTTER;
  root.classList.toggle(REFLOW_CLASS, reflow);
  if (!reflow) {
    if (!layout.reflowed) return false; // a page that fits is never touched
    layout.reflowed = false;
    unpinColumns(layout, root);
    for (const i of layout.indents) i.el.style.marginLeft = i.inline;
    for (const t of layout.tables) {
      t.el.classList.remove(WIDE_TABLE_CLASS);
      t.el.style.width = t.inline;
      for (const part of t.parts) part.el.style.width = part.inline;
    }
    for (const i of layout.images) i.el.style.setProperty('zoom', i.inline);
    return false;
  }

  layout.reflowed = true;
  const article = root.querySelector<HTMLElement>('.docx-wrapper > section > article');
  const room = article?.clientWidth || width;
  const scale = Math.min(1, Math.max(MIN_INDENT_SCALE, room / layout.text));
  for (const i of layout.indents) i.el.style.marginLeft = `${Math.max(i.margin * scale, i.hang)}px`;
  for (const t of layout.tables) {
    t.el.style.width = `${t.percent}%`;
    for (const part of t.parts) part.el.style.width = `${part.percent}%`;
  }
  // Images last: the room each has depends on the tables and indents above.
  const view = root.ownerDocument.defaultView!;
  for (const i of layout.images) {
    i.el.style.setProperty('zoom', i.inline);
    const host = i.el.parentElement?.closest<HTMLElement>('td, th, p, li, article');
    if (!host) continue;
    const s = view.getComputedStyle(host);
    const space = host.clientWidth - px(s.paddingLeft) - px(s.paddingRight) - Math.max(0, px(s.textIndent));
    if (space > 0 && i.width > space) i.el.style.setProperty('zoom', String(space / i.width));
  }
  // A table still wider than its room scrolls sideways on its own (the page would hide the rest).
  unpinColumns(layout, root);
  for (const t of layout.tables) {
    t.el.classList.remove(WIDE_TABLE_CLASS);
    const room = t.el.parentElement?.clientWidth ?? 0;
    if (room > 0 && t.el.offsetWidth > room + 1) t.el.classList.add(WIDE_TABLE_CLASS);
  }
  // Then, per table that scrolls: its first column pinned where that is safe, and which sides have more.
  for (const t of layout.tables) {
    if (!t.el.classList.contains(WIDE_TABLE_CLASS)) continue;
    pinFirstColumn(t.el);
    markTableOverflow(t.el);
  }
  return true;
}

// ---- Tables that scroll sideways --------------------------------------------------------------------
// A table too wide for a narrow pane scrolls on its own (src-docx-wide). The frame CSS fades the side
// that has more columns (src-docx-more-left / -right, kept current as it scrolls) and, on touch screens,
// shows a hint above it. Its first column - the row's name, as a rule - is pinned while the rest
// scrolls, but only where that cannot mislead: a top-level, left-to-right table of three or more
// columns whose first column is narrow, and only cells that truly sit in that column alone (merged
// cells are walked as a grid; a cell spanning into the second column scrolls with the rest).

const PINNED_CLASS = 'src-docx-pinned';
const STICKY_CLASS = 'src-docx-sticky';
const MORE_LEFT = 'src-docx-more-left';
const MORE_RIGHT = 'src-docx-more-right';
// The pinned column may cover at most this share of what the table shows.
const MAX_PINNED_SHARE = 0.4;

// The cells starting in a table's first column and spanning only it, and how many columns it has.
export function firstColumnCells(table: HTMLTableElement): { cells: HTMLTableCellElement[]; columns: number } {
  const taken = new Map<number, Set<number>>();
  const cells: HTMLTableCellElement[] = [];
  let columns = 0;
  Array.from(table.rows).forEach((row, r) => {
    let col = 0;
    for (const cell of Array.from(row.cells)) {
      while (taken.get(r)?.has(col)) col++;
      const span = Math.max(1, cell.colSpan);
      if (col === 0 && span === 1) cells.push(cell);
      for (let dr = 0; dr < Math.max(1, cell.rowSpan); dr++) {
        const set = taken.get(r + dr) ?? new Set<number>();
        for (let dc = 0; dc < span; dc++) set.add(col + dc);
        taken.set(r + dr, set);
      }
      col += span;
      columns = Math.max(columns, col);
    }
  });
  return { cells, columns };
}

const opaque = (color: string) => {
  const m = /^rgba?\(([^)]+)\)$/.exec(color.trim());
  if (!m) return color !== 'transparent' && color !== '';
  const parts = m[1].split(/[\s,/]+/).filter(Boolean);
  return parts.length < 4 || parseFloat(parts[3]) >= 1;
};

// What shows behind a cell: the first solid background from the cell up to the page (white if none).
function backdrop(cell: HTMLElement, view: Window): string {
  for (let el: HTMLElement | null = cell; el && el.tagName !== 'BODY'; el = el.parentElement) {
    const color = view.getComputedStyle(el).backgroundColor;
    if (color && opaque(color)) return color;
  }
  return '#ffffff';
}

function unpinColumns(layout: DocxLayout, root: ParentNode) {
  for (const cell of Array.from(root.querySelectorAll<HTMLElement>(`.${STICKY_CLASS}`))) {
    cell.classList.remove(STICKY_CLASS);
    cell.style.removeProperty('--src-sticky-bg');
  }
  for (const t of layout.tables) t.el.classList.remove(PINNED_CLASS, MORE_LEFT, MORE_RIGHT);
}

function pinFirstColumn(table: HTMLTableElement) {
  const view = table.ownerDocument.defaultView;
  if (!view || table.parentElement?.closest('td, th') || view.getComputedStyle(table).direction === 'rtl') return;
  const { cells, columns } = firstColumnCells(table);
  if (columns < 3 || cells.length === 0) return;
  const width = Math.max(...cells.map((c) => c.offsetWidth));
  if (width <= 0 || width > table.clientWidth * MAX_PINNED_SHARE) return;
  for (const cell of cells) {
    cell.style.setProperty('--src-sticky-bg', backdrop(cell, view));
    cell.classList.add(STICKY_CLASS);
  }
  table.classList.add(PINNED_CLASS);
}

// Marks which sides of a sideways-scrolling table have columns out of view.
export function markTableOverflow(table: HTMLElement) {
  const max = table.scrollWidth - table.clientWidth;
  table.classList.toggle(MORE_LEFT, max > 1 && table.scrollLeft > 1);
  table.classList.toggle(MORE_RIGHT, max > 1 && table.scrollLeft < max - 1);
}

// Scrolls a sideways-scrolling table so the cited element is in view, clear of the pinned column.
export function revealInTable(target: HTMLElement) {
  const table = target.closest<HTMLTableElement>(`table.${WIDE_TABLE_CLASS}`);
  if (!table) return;
  if (!target.closest(`.${STICKY_CLASS}`)) {
    const pinned = Array.from(table.rows).flatMap((row) => Array.from(row.cells)).find((c) => c.classList.contains(STICKY_CLASS));
    const cover = pinned?.offsetWidth ?? 0;
    const left = target.getBoundingClientRect().left - table.getBoundingClientRect().left + table.scrollLeft;
    table.scrollLeft = Math.max(0, left - cover - 16);
  }
  markTableOverflow(table);
}
