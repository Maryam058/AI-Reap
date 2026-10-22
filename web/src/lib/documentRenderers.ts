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

// Renders the DOCX into `body` and its styles into `styles` (both in the viewer's iframe document),
// made inert first. Returns the body paragraphs in document order - the paragraphs the text was
// extracted from - leaving out headers, footers and notes, which the extracted text does not have.
export async function renderDocx(data: ArrayBuffer, body: HTMLElement, styles: HTMLElement): Promise<HTMLElement[]> {
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
  return Array.from(body.querySelectorAll<HTMLElement>('section article p')).filter((p) => !p.closest('li'));
}
