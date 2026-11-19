import { useEffect, useRef } from 'react';
import type { SourceReference } from '../api/types';
import { markOccurrence } from '../lib/documentHighlight';
import { fitDocxToWidth, markTableOverflow, measureDocxLayout, openPdf, renderDocx, revealInTable } from '../lib/documentRenderers';
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

  /* A pane narrower than the page (fitDocxToWidth): the page reflows to the pane - no fixed page width,
     Word margins or page height, and left-aligned, so nothing is cut off on either side. */
  html.src-docx-reflow .docx-wrapper { display: block !important; padding: 8px !important; }
  html.src-docx-reflow .docx-wrapper > section.docx {
    width: auto !important; min-height: 0 !important; padding: 20px 16px !important; margin: 0 0 12px !important;
  }
  html.src-docx-reflow .docx-wrapper > section.docx > header { margin-top: 0 !important; min-height: 0 !important; }
  html.src-docx-reflow .docx-wrapper > section.docx > footer { margin-bottom: 0 !important; min-height: 0 !important; }
  html.src-docx-reflow .docx-wrapper table { max-width: 100%; }
  /* A word longer than its column breaks rather than widening the table past the pane - but a column
     keeps a readable width, so most words stay whole. */
  html.src-docx-reflow .docx-wrapper :is(td, th, td *, th *) { overflow-wrap: anywhere; }
  html.src-docx-reflow .docx-wrapper :is(td, th) { min-width: 5em; }
  /* A table that still cannot fit (an image in a cell, many columns) scrolls sideways on its own -
     the page hides what overflows it. */
  html.src-docx-reflow .docx-wrapper table.src-docx-wide {
    display: block; overflow-x: auto; overscroll-behavior-x: contain; position: relative;
  }
  /* The side with more columns out of view fades softly (documentRenderers: markTableOverflow). */
  html.src-docx-reflow table.src-docx-wide.src-docx-more-right {
    -webkit-mask-image: linear-gradient(to right, #000 calc(100% - 28px), rgba(0, 0, 0, 0.3));
    mask-image: linear-gradient(to right, #000 calc(100% - 28px), rgba(0, 0, 0, 0.3));
  }
  html.src-docx-reflow table.src-docx-wide.src-docx-more-left:not(.src-docx-pinned) {
    -webkit-mask-image: linear-gradient(to left, #000 calc(100% - 28px), rgba(0, 0, 0, 0.3));
    mask-image: linear-gradient(to left, #000 calc(100% - 28px), rgba(0, 0, 0, 0.3));
  }
  html.src-docx-reflow table.src-docx-wide.src-docx-more-left.src-docx-more-right:not(.src-docx-pinned) {
    -webkit-mask-image: linear-gradient(to right, rgba(0, 0, 0, 0.3), #000 28px, #000 calc(100% - 28px), rgba(0, 0, 0, 0.3));
    mask-image: linear-gradient(to right, rgba(0, 0, 0, 0.3), #000 28px, #000 calc(100% - 28px), rgba(0, 0, 0, 0.3));
  }
  /* The first column stays put while the rest scrolls under it, on the page's own background. */
  html.src-docx-reflow table.src-docx-wide .src-docx-sticky {
    position: sticky; left: 0; z-index: 1; background-color: var(--src-sticky-bg, #fff);
  }
  /* Scrolled: a clear edge, so the columns sliding under it do not read as part of the pinned one. */
  html.src-docx-reflow table.src-docx-wide.src-docx-more-left .src-docx-sticky {
    box-shadow: inset -2px 0 0 rgba(0, 0, 0, 0.28), 5px 0 6px -2px rgba(0, 0, 0, 0.22);
  }
  html.src-docx-reflow tr.src-docx-cited > .src-docx-sticky {
    background-image: linear-gradient(rgba(255, 213, 79, 0.28), rgba(255, 213, 79, 0.28));
  }
  /* Touch screens show no scrollbar until the table moves: say that it does. Drawn by CSS, so it is
     not part of the document's text. */
  @media (hover: none) and (pointer: coarse) {
    html.src-docx-reflow table.src-docx-wide::before {
      content: "\\2194\\FE0E  Swipe horizontally to view all columns";
      content: "\\2194\\FE0E  Swipe horizontally to view all columns" / "";
      display: block; position: sticky; left: 0; width: max-content; max-width: calc(100% - 32px); box-sizing: border-box;
      margin: 0 0 4px; padding: 2px 8px; border-radius: 10px; background: #eef1f4; color: #4a5361;
      font: 500 11px/16px system-ui, -apple-system, "Segoe UI", sans-serif; letter-spacing: 0; white-space: normal;
    }
  }
`;

// The DOCX itself, rendered from its own structure (headings, paragraphs, tables, styles) inside a
// sandboxed iframe that cannot run scripts or navigate, and scrolled to the cited paragraphs. A
// paragraph is cited only when its text matches the cited line of the extracted text.
export function DocxOriginalView({ data, lines, reference, location, title, onNavigated, onFailed }: ViewProps) {
  const frameRef = useRef<HTMLIFrameElement>(null);

  useEffect(() => {
    const frame = frameRef.current!;
    let cancelled = false;
    let resizes: ResizeObserver | undefined;
    let stopTracking: (() => void) | undefined;
    let stopSideways: (() => void) | undefined;

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

        // Narrower than the page: reflow it to the pane before finding the cited place.
        const layout = measureDocxLayout(doc.body);
        const fit = () => layout && fitDocxToWidth(layout, doc.documentElement, doc.documentElement.clientWidth);
        fit();
        let cited: HTMLElement | null = null;

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
            cited = target;
            const win = frame.contentWindow!;
            win.scrollTo(0, Math.max(0, target.getBoundingClientRect().top + win.scrollY - 24));
            // In a table that scrolls sideways, bring the cited cell into view too.
            revealInTable(target);
            result = { status: 'highlighted' };
          }
        }
        onNavigated(result);

        // A table that scrolls sideways (mouse, trackpad or touch): keep its faded edges current.
        const sideways = (event: Event) => {
          const el = event.target as Element | null;
          if (el?.matches?.('table.src-docx-wide')) markTableOverflow(el as HTMLElement);
        };
        doc.addEventListener('scroll', sideways, { capture: true, passive: true });
        stopSideways = () => doc.removeEventListener('scroll', sideways, { capture: true });

        // The pane resized (the window, or panels added beside it): fit the page again and keep the
        // same place in view - the cited passage when it was in view, else what was at the top.
        const win = frame.contentWindow;
        if (layout && win && typeof ResizeObserver !== 'undefined') {
          let anchor: { el: Element; top: number } | null = null;
          const remember = () => {
            const height = doc.documentElement.clientHeight;
            const box = cited?.getBoundingClientRect();
            if (cited && box && box.bottom > 0 && box.top < height) return void (anchor = { el: cited, top: box.top });
            for (let y = 8; y < Math.min(height, 240); y += 24) {
              const el = doc.elementFromPoint(doc.documentElement.clientWidth / 2, y);
              if (el && el.closest('section.docx > *')) return void (anchor = { el, top: el.getBoundingClientRect().top });
            }
          };
          remember();
          win.addEventListener('scroll', remember, { passive: true });
          stopTracking = () => win.removeEventListener('scroll', remember);
          let width = doc.documentElement.clientWidth;
          resizes = new ResizeObserver(() => {
            if (doc.documentElement.clientWidth === width) return;
            width = doc.documentElement.clientWidth;
            const keep = anchor;
            fit();
            if (keep?.el.isConnected) win.scrollTo(0, Math.max(0, win.scrollY + keep.el.getBoundingClientRect().top - keep.top));
            if (cited && keep?.el === cited) revealInTable(cited);
          });
          resizes.observe(frame);
        }
      } catch {
        if (!cancelled) onFailed();
      }
    })();

    return () => {
      cancelled = true;
      resizes?.disconnect();
      stopTracking?.();
      stopSideways?.();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data]);

  // No allow-scripts, allow-forms, allow-popups or allow-top-navigation: the document is inert and
  // cannot leave the frame. allow-same-origin only lets the viewer reach in to render and scroll it.
  return <iframe ref={frameRef} className="src-original src-docx" sandbox="allow-same-origin" title={title} tabIndex={0} data-autofocus />;
}
