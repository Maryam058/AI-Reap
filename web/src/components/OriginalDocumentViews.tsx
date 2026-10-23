import { useEffect, useRef } from 'react';
import type { SourceReference } from '../api/types';
import { markOccurrence } from '../lib/documentHighlight';
import { openPdf, renderDocx } from '../lib/documentRenderers';
import { alignParagraphs, matchesBefore, normalize, pageForLine, type ReferenceLocation } from '../lib/sourceReferences';

// How far the view got to the cited place - shown to the user as is (SourceDocumentViewer).
export type NavigationResult =
  | { status: 'highlighted'; page?: number }
  | { status: 'page-only'; page: number } // on the right page, but its text could not be matched there
  | { status: 'not-found' }; // opened at the start; nothing is highlighted

interface ViewProps {
  data: ArrayBuffer;
  lines: readonly string[];
  reference: SourceReference;
  // Where the reference is in the extracted text, already verified against it (locateReference).
  location: ReferenceLocation;
  title: string;
  onNavigated: (result: NavigationResult) => void;
  onFailed: () => void;
}

const isQuote = (reference: SourceReference) => !!(reference.excerptIsQuote && reference.excerpt);

// Scrolls only the given pane (never the page behind the dialog) so the element is near the top.
function scrollPaneTo(pane: HTMLElement, target: HTMLElement) {
  const offset = target.getBoundingClientRect().top - pane.getBoundingClientRect().top + pane.scrollTop;
  pane.scrollTop = Math.max(0, offset - 24);
}

// The PDF itself, page by page, opened at the page the cited line is on (from the page map recorded at
// upload), with the cited heading or quote marked in that page's text layer. Pages are drawn as they
// come near the visible area.
export function PdfOriginalView({ data, lines, reference, location, title, pageLineStarts, onNavigated, onFailed }: ViewProps & { pageLineStarts?: number[] | null }) {
  const paneRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const pane = paneRef.current!;
    let cancelled = false;
    let observer: IntersectionObserver | undefined;
    let handle: Awaited<ReturnType<typeof openPdf>> | undefined;

    (async () => {
      try {
        handle = await openPdf(data);
        if (cancelled) return handle.destroy();

        const first = await handle.pageSize(1);
        const width = pane.clientWidth > 0 ? pane.clientWidth - 32 : first.width;
        const scale = Math.min(2, Math.max(0.5, width / first.width));

        const pages: HTMLDivElement[] = [];
        const rendered = new Map<number, Promise<HTMLElement>>();
        for (let n = 1; n <= handle.pageCount; n++) {
          const page = document.createElement('div');
          page.className = 'src-pdf-page';
          page.dataset.page = String(n);
          page.setAttribute('role', 'region');
          page.setAttribute('aria-label', `Page ${n} of ${handle.pageCount}`);
          page.style.width = `${Math.floor(first.width * scale)}px`;
          page.style.height = `${Math.floor(first.height * scale)}px`;
          pages.push(page);
        }
        pane.replaceChildren(...pages);
        const render = (n: number) => {
          if (!rendered.has(n)) rendered.set(n, handle!.renderPage(n, pages[n - 1], scale));
          return rendered.get(n)!;
        };

        const located = location.status === 'located' ? location : null;
        const target = located ? pageForLine(pageLineStarts, isQuote(reference) ? located.line : (located.headingLine ?? located.line)) : null;
        const page = target !== null && target <= handle.pageCount ? target : null;

        let result: NavigationResult = { status: 'not-found' };
        if (located && page !== null) {
          const textLayer = await render(page);
          if (cancelled) return;
          const pageStart = pageLineStarts![page - 1];
          // The match on the cited line, not the first on the page: a repeated heading ("Scope") or a
          // heading's words earlier in the text are counted past.
          const markLine = (line: number, text: string, className?: string) =>
            markOccurrence(textLayer, text, matchesBefore(lines, pageStart, line, text), className);
          const marks = isQuote(reference)
            ? markLine(located.line, reference.excerpt!)
            : markLine(located.headingLine ?? located.line, lines[located.headingLine ?? located.line]);
          if (isQuote(reference) && located.headingLine !== null && pageForLine(pageLineStarts, located.headingLine) === page) {
            markLine(located.headingLine, lines[located.headingLine], 'src-hit src-hit-heading');
          }
          if (marks.length > 0) {
            marks[0].setAttribute('aria-current', 'location');
            scrollPaneTo(pane, marks[0]);
            result = { status: 'highlighted', page };
          } else {
            scrollPaneTo(pane, pages[page - 1]);
            result = { status: 'page-only', page };
          }
        } else {
          await render(1);
        }
        if (cancelled) return;
        onNavigated(result);

        if (typeof IntersectionObserver !== 'undefined') {
          observer = new IntersectionObserver(
            (entries) => entries.forEach((e) => e.isIntersecting && void render(Number((e.target as HTMLElement).dataset.page)).catch(() => {})),
            { root: pane, rootMargin: '800px 0px' },
          );
          pages.forEach((p) => observer!.observe(p));
        }
      } catch {
        if (!cancelled) onFailed();
      }
    })();

    return () => {
      cancelled = true;
      observer?.disconnect();
      handle?.destroy();
    };
    // The document and reference are fixed for the life of a viewer.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data]);

  return <div className="src-original src-pdf" ref={paneRef} tabIndex={0} data-autofocus role="document" aria-label={title} />;
}

const DOCX_FRAME_STYLE = `
  html, body { margin: 0; background: #e9ebef; }
  .docx-wrapper { background: transparent !important; padding: 16px !important; }
  .src-docx-cited { background: rgba(255, 213, 79, 0.28); box-shadow: -4px 0 0 #f5b400; }
  .src-docx-heading { box-shadow: -4px 0 0 #c98a00; }
  mark.src-hit { background: #ffd54f; color: inherit; padding: 0 1px; border-radius: 2px; }
`;

// The DOCX itself, rendered from its own structure (headings, paragraphs, tables, styles) inside a
// sandboxed iframe that cannot run scripts or navigate, and scrolled to the cited paragraphs. A
// paragraph is cited only when its text matches the cited line of the extracted text.
export function DocxOriginalView({ data, lines, reference, location, title, onNavigated, onFailed }: ViewProps) {
  const frameRef = useRef<HTMLIFrameElement>(null);

  useEffect(() => {
    const frame = frameRef.current!;
    let cancelled = false;

    (async () => {
      try {
        const doc = frame.contentDocument;
        if (!doc) throw new Error('The document frame is not available.');
        doc.open();
        doc.write('<!doctype html><html><head><meta charset="utf-8"></head><body></body></html>');
        doc.close();
        const own = doc.createElement('style');
        own.textContent = DOCX_FRAME_STYLE;
        const styles = doc.createElement('div');
        doc.head.append(styles, own);

        const rendered = await renderDocx(data, doc.body, styles);
        if (cancelled) return;

        let result: NavigationResult = { status: 'not-found' };
        if (location.status === 'located') {
          // A section is cited from its heading; a quote only by its own paragraphs, with the heading of
          // its section marked apart - never the whole section above it.
          const endLine = Math.max(location.endLine, location.line + 1);
          // Paragraphs and whole table rows, as text is extracted now; else one paragraph per line, as
          // sources uploaded before table rows were kept together were extracted.
          let paragraphs = rendered.blocks.map((b) => b.element);
          let texts = rendered.blocks.map((b) => b.text);
          let span = alignParagraphs(texts, lines, location.line, endLine);
          if (!span) {
            paragraphs = rendered.paragraphs.map((b) => b.element);
            texts = rendered.paragraphs.map((b) => b.text);
            span = alignParagraphs(texts, lines, location.line, endLine);
          }
          if (span) {
            for (let p = span.first; p <= span.last; p++) paragraphs[p].classList.add('src-docx-cited');
            const heading =
              isQuote(reference) && location.headingLine !== null && location.headingLine < location.line
                ? alignParagraphs(texts, lines, location.headingLine, location.headingLine + 1)
                : null;
            paragraphs[heading && heading.first < span.first ? heading.first : span.first].classList.add('src-docx-heading');
            let target: HTMLElement = paragraphs[span.first];
            if (isQuote(reference)) {
              const quote = normalize(reference.excerpt!);
              const holder = paragraphs.slice(span.first, span.last + 1).find((p) => normalize(p.textContent ?? '').includes(quote));
              const marks = holder ? markOccurrence(holder, reference.excerpt!) : [];
              if (marks[0]) target = marks[0];
            }
            target.setAttribute('aria-current', 'location');
            const win = frame.contentWindow!;
            win.scrollTo(0, Math.max(0, target.getBoundingClientRect().top + win.scrollY - 24));
            result = { status: 'highlighted' };
          }
        }
        onNavigated(result);
      } catch {
        if (!cancelled) onFailed();
      }
    })();

    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data]);

  // No allow-scripts, allow-forms, allow-popups or allow-top-navigation: the document is inert and
  // cannot leave the frame. allow-same-origin only lets the viewer reach in to render and scroll it.
  return <iframe ref={frameRef} className="src-original src-docx" sandbox="allow-same-origin" title={title} tabIndex={0} data-autofocus />;
}
