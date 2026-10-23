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
