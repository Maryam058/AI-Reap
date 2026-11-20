import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type ReactNode } from 'react';
import type { SourceReference } from '../api/types';
import { markAcross, markOccurrence, unmarkAll } from '../lib/documentHighlight';
import { ZOOM_LEVELS, zoomForKey, zoomStep } from '../lib/documentZoom';
import {
  fitDocxToWidth,
  markTableOverflow,
  measureDocxLayout,
  openPdf,
  renderDocx,
  revealInTable,
  type DocxLayout,
  type PdfHandle,
  type RenderedDocx,
} from '../lib/documentRenderers';
import {
  alignParagraphs,
  highlightTarget,
  matchesBefore,
  pageForLine,
  type HighlightTarget,
  type ReferenceLocation,
  type RelatedPassage,
} from '../lib/sourceReferences';
import { IconMinus, IconPlus } from './icons';

// How far the view got to the cited place - shown to the user as is (SourceDocumentViewer).
export type NavigationResult =
  | { status: 'highlighted'; page?: number }
  | { status: 'page-only'; page: number } // on the right page, but its text could not be matched there
  | { status: 'paragraph-only' } // at the cited paragraphs, but the quoted words could not be matched in them
  | { status: 'not-found' }; // opened at the start; nothing is highlighted

// The yellow highlight: the shown passage's exact text (highlightTarget), and the question's other
// passages in the same source more lightly. A quote's section heading is underlined, not highlighted.
export const HIT = 'src-hit';
export const RELATED_HIT = 'src-hit src-hit-related';
export const HEADING_MARK = 'src-heading-mark';

const NONE: readonly RelatedPassage[] = [];

interface ViewProps {
  data: ArrayBuffer;
  lines: readonly string[];
  // The passage shown. It can change while the view is open (stepping between a question's passages
  // in the same source): the document stays loaded and only the highlight and scroll position move.
  reference: SourceReference;
  // Where the reference is in the extracted text, already verified against it (locateReference).
  location: ReferenceLocation;
  // The question's other passages located in this same source (relatedPassages).
  related?: readonly RelatedPassage[];
  title: string;
  onNavigated: (result: NavigationResult) => void;
  onFailed: () => void;
}

// Scrolls only the given pane (never the page behind the dialog) so the element is near the top.
function scrollPaneTo(pane: HTMLElement, target: HTMLElement) {
  const offset = target.getBoundingClientRect().top - pane.getBoundingClientRect().top + pane.scrollTop;
  pane.scrollTop = Math.max(0, offset - 24);
}

// Ctrl/Cmd + wheel (and a trackpad pinch, which browsers send as one) zooms the document, a step at a
// time however many wheel events a gesture sends.
function wheelZoom(target: EventTarget, setZoom: (update: (zoom: number) => number) => void): () => void {
  let last = 0;
  const onWheel = (event: Event) => {
    const wheel = event as WheelEvent;
    if (!wheel.ctrlKey && !wheel.metaKey) return;
    wheel.preventDefault();
    const now = Date.now();
    if (now - last < 160 || wheel.deltaY === 0) return;
    last = now;
    setZoom((z) => zoomStep(z, wheel.deltaY < 0 ? 1 : -1));
  };
  target.addEventListener('wheel', onWheel, { passive: false });
  return () => target.removeEventListener('wheel', onWheel);
}

// The bar above a document: zoom out / level / zoom in / reset, and what the view adds (the page in view).
function DocumentBar({ zoom, onZoom, label, children }: { zoom: number; onZoom: (zoom: number) => void; label: string; children?: ReactNode }) {
  const percent = `${Math.round(zoom * 100)}%`;
  return (
    <div className="src-original-bar" role="toolbar" aria-label={`${label} controls`}>
      <div className="src-zoom" role="group" aria-label="Zoom">
        <button type="button" className="icon-btn" onClick={() => onZoom(zoomStep(zoom, -1))} disabled={zoom <= ZOOM_LEVELS[0]} aria-label="Zoom out" title="Zoom out (Ctrl −)">
          <IconMinus width={16} height={16} aria-hidden="true" />
        </button>
        <output className="src-zoom-level" aria-live="polite" aria-label={`Zoom ${percent}`}>
          {percent}
        </output>
        <button
          type="button"
          className="icon-btn"
          onClick={() => onZoom(zoomStep(zoom, 1))}
          disabled={zoom >= ZOOM_LEVELS[ZOOM_LEVELS.length - 1]}
          aria-label="Zoom in"
          title="Zoom in (Ctrl +)"
        >
          <IconPlus width={16} height={16} aria-hidden="true" />
        </button>
        <button type="button" className="btn btn-ghost btn-sm" onClick={() => onZoom(1)} disabled={zoom === 1} title="Back to 100% (Ctrl 0)">
          Reset zoom
        </button>
      </div>
      {children}
    </div>
  );
}

// One zoom level's pages of a PDF: laid out at once (so the pane scrolls through the whole document),
// drawn as they come near the visible area. decorate marks a drawn page's text layer for the passage
// being shown; it is replaced when the passage changes and run again on every page as it is drawn.
interface PdfLayout {
  pages: HTMLDivElement[];
  layers: Map<number, HTMLElement>;
  render: (n: number) => Promise<HTMLElement>;
  observe: () => void;
}
type Decorate = (n: number, layer: HTMLElement) => HTMLElement[];

// The PDF itself, page by page, opened at the page the cited line is on (from the page map recorded at
// upload), with the cited heading or quote marked in that page's text layer. The PDF is parsed once;
// zooming lays the pages out again at the new size and keeps the place being read, and stepping to
// another passage only moves the highlight.
export function PdfOriginalView(props: ViewProps & { pageLineStarts?: number[] | null }) {
  const { data, reference, location, related = NONE, title } = props;
  const paneRef = useRef<HTMLDivElement>(null);
  const latest = useRef(props);
  useLayoutEffect(() => {
    latest.current = props;
  });
  const [handle, setHandle] = useState<PdfHandle | null>(null);
  const [zoom, setZoom] = useState(1);
  const [layout, setLayout] = useState<PdfLayout | null>(null);
  const [pageInView, setPageInView] = useState(1);
  const pageInViewRef = useRef(1);
  const showPage = (n: number) => {
    pageInViewRef.current = n;
    setPageInView(n);
  };
  // The passage the pane was last scrolled to: a new layout for the same passage is a zoom, which keeps
  // the reader's place instead of jumping back to the passage.
  const navigated = useRef<readonly unknown[] | null>(null);
  // Marks a drawn page for the passage being shown; replaced when the passage changes, and run on every
  // page as it is drawn (at any zoom).
  const decorate = useRef<Decorate>(() => []);

  useEffect(() => {
    let cancelled = false;
    let opened: PdfHandle | undefined;
    openPdf(data).then(
      (h) => {
        opened = h;
        if (cancelled) h.destroy();
        else setHandle(h);
      },
      () => !cancelled && latest.current.onFailed(),
    );
    return () => {
      cancelled = true;
      opened?.destroy();
    };
  }, [data]);

  useEffect(() => {
    if (!handle) return;
    const pane = paneRef.current!;
    let cancelled = false;
    let observer: IntersectionObserver | undefined;
    (async () => {
      try {
        const first = await handle.pageSize(1);
        if (cancelled) return;
        const width = pane.clientWidth > 0 ? pane.clientWidth - 32 : first.width;
        const scale = Math.min(2, Math.max(0.5, width / first.width)) * zoom;
        // The place being read, kept across a zoom: the page at the top of the pane and how far down it.
        const paneTop = pane.getBoundingClientRect().top;
        const shown = Array.from(pane.querySelectorAll<HTMLElement>('.src-pdf-page'));
        const reading = shown.find((p) => p.getBoundingClientRect().bottom > paneTop);
        const place = reading && reading.offsetHeight > 0 ? { n: Number(reading.dataset.page), within: (paneTop - reading.getBoundingClientRect().top) / reading.offsetHeight } : null;

        const pages: HTMLDivElement[] = [];
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
        if (place && pages[place.n - 1]) {
          const page = pages[place.n - 1];
          pane.scrollTop = Math.max(0, page.getBoundingClientRect().top - pane.getBoundingClientRect().top + pane.scrollTop + place.within * page.offsetHeight);
        }

        const rendered = new Map<number, Promise<HTMLElement>>();
        const next: PdfLayout = {
          pages,
          layers: new Map(),
          render: (n) => {
            if (!rendered.has(n)) {
              rendered.set(
                n,
                handle.renderPage(n, pages[n - 1], scale).then((layer) => {
                  if (!cancelled) {
                    next.layers.set(n, layer);
                    decorate.current(n, layer);
                  }
                  return layer;
                }),
              );
            }
            return rendered.get(n)!;
          },
          // Started once the pane is at the passage, so the pages drawn first are the ones in view.
          observe: () => {
            if (observer || cancelled || typeof IntersectionObserver === 'undefined') return;
            observer = new IntersectionObserver(
              (entries) => entries.forEach((e) => e.isIntersecting && void next.render(Number((e.target as HTMLElement).dataset.page)).catch(() => {})),
              { root: pane, rootMargin: '800px 0px' },
            );
            pages.forEach((p) => observer!.observe(p));
          },
        };
        setLayout(next);
      } catch {
        if (!cancelled) latest.current.onFailed();
      }
    })();
    return () => {
      cancelled = true;
      observer?.disconnect();
    };
  }, [handle, zoom]);

  useEffect(() => {
    if (!layout) return;
    const pane = paneRef.current!;
    const { lines, pageLineStarts, onNavigated, onFailed } = latest.current;
    let cancelled = false;

    const pageOf = (line: number) => {
      const n = pageForLine(pageLineStarts, line);
      return n !== null && n <= layout.pages.length ? n : null;
    };
    // The match on the target's own line, not the first on the page: a repeated heading ("Scope")
    // or the same words earlier on the page are counted past.
    const markOn = (layer: HTMLElement, n: number, target: HighlightTarget, className: string) =>
      markOccurrence(layer, target.text, matchesBefore(lines, pageLineStarts![n - 1], target.line, target.text), className);
    const relatedOn = new Map<number, HighlightTarget[]>();
    for (const r of related) {
      const n = pageOf(r.target.line);
      if (n !== null) relatedOn.set(n, [...(relatedOn.get(n) ?? []), r.target]);
    }

    const located = location.status === 'located' ? location : null;
    const target = located ? highlightTarget(lines, reference, location) : null;
    const page = target ? pageOf(target.line) : null;
    const headingLine = target?.kind === 'quote' && located?.headingLine != null && pageOf(located.headingLine) === page ? located.headingLine : null;
    const heading = headingLine !== null ? lines[headingLine]?.trim() : '';

    const mark: Decorate = (n, layer) => {
      unmarkAll(layer);
      for (const t of relatedOn.get(n) ?? []) markOn(layer, n, t, RELATED_HIT);
      if (!target || n !== page) return [];
      const marks = markOn(layer, n, target, HIT);
      if (heading) markOn(layer, n, { kind: 'heading', line: headingLine!, endLine: headingLine! + 1, text: heading }, HEADING_MARK);
      marks[0]?.setAttribute('aria-current', 'location');
      return marks;
    };
    decorate.current = mark;
    // Pages already drawn lose the previous passage's marks and get this one's.
    for (const [n, layer] of layout.layers) mark(n, layer);

    const key = [reference, location, related] as const;
    const zoomed = navigated.current !== null && key.every((k, i) => k === navigated.current![i]);
    if (zoomed) {
      // The page being read is drawn at the new size at once (with the passage's marks if it is on it).
      void layout.render(Math.min(pageInViewRef.current, layout.pages.length)).catch(() => {});
      layout.observe();
      return;
    }

    (async () => {
      try {
        let result: NavigationResult = { status: 'not-found' };
        if (target && page !== null) {
          const layer = await layout.render(page);
          if (cancelled) return;
          const marks = mark(page, layer);
          if (marks.length > 0) {
            scrollPaneTo(pane, marks[0]);
            result = { status: 'highlighted', page };
          } else {
            scrollPaneTo(pane, layout.pages[page - 1]);
            result = { status: 'page-only', page };
          }
          showPage(page);
        } else {
          await layout.render(1);
          if (cancelled) return;
          pane.scrollTop = 0;
          showPage(1);
        }
        navigated.current = key;
        onNavigated(result);
        layout.observe();
      } catch {
        if (!cancelled) onFailed();
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [layout, reference, location, related]);

  // The page in view, for the bar: the last page whose top is above the upper third of the pane.
  useEffect(() => {
    if (!layout) return;
    const pane = paneRef.current!;
    const update = () => {
      const line = pane.getBoundingClientRect().top + pane.clientHeight / 3;
      let n = 1;
      // The pages in the pane now (a zoom replaces them; the previous ones are no longer in the page).
      for (const p of Array.from(pane.querySelectorAll<HTMLElement>('.src-pdf-page'))) {
        if (p.getBoundingClientRect().top > line) break;
        n = Number(p.dataset.page);
      }
      showPage(n);
    };
    pane.addEventListener('scroll', update, { passive: true });
    return () => pane.removeEventListener('scroll', update);
  }, [layout]);

  useEffect(() => wheelZoom(paneRef.current!, setZoom), []);

  const onKeyDown = (event: ReactKeyboardEvent) => {
    const next = zoomForKey(zoom, event);
    if (next === null) return;
    event.preventDefault();
    setZoom(next);
  };

  return (
    <div className="src-original-frame">
      <DocumentBar zoom={zoom} onZoom={setZoom} label="PDF">
        {layout && (
          <span className="src-original-page" aria-live="polite">
            Page {Math.min(pageInView, layout.pages.length)} of {layout.pages.length}
          </span>
        )}
      </DocumentBar>
      <div className="src-original src-pdf" ref={paneRef} tabIndex={0} data-autofocus role="document" aria-label={title} onKeyDown={onKeyDown} />
    </div>
  );
}

const DOCX_FRAME_STYLE = `
  html, body { margin: 0; background: #e9ebef; }
  .docx-wrapper { background: transparent !important; padding: 16px !important; }
  /* Zoom (DocxOriginalView): the whole rendered document grows or shrinks; its own layout is kept. */
  .docx-wrapper { zoom: var(--src-zoom, 1); }
  /* A page wider than the pane (zoomed in) starts at the left edge and scrolls, rather than being
     centred past the edge where it could not be scrolled to. */
  .docx-wrapper { align-items: safe center !important; }
  /* The cited paragraphs get a bar beside them, not a fill: only the passage's own words are yellow. */
  .src-docx-cited { box-shadow: -4px 0 0 #e0b400; }
  .src-docx-heading { box-shadow: -4px 0 0 #7a6a2a; }
  /* The marks keep the document's own font, size and line height; padding is drawn by box-shadow so
     no line moves. */
  /* No outline: a quote over several formatting runs is one mark per run, and outlines would draw it
     as a row of boxes - the fill alone reads as one passage. */
  mark.src-hit {
    background: #ffe45c; color: inherit; font: inherit; border-radius: 0;
    -webkit-box-decoration-break: clone; box-decoration-break: clone;
  }
  mark.src-hit-related { background: #fff3b0; box-shadow: none; }
  mark.src-hit-related mark.src-hit:not(.src-hit-related) { background: #ffe45c; }

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

// A rendered Word document, kept while the view is open: the passage, zoom and pane width change
// around it without rendering it again.
interface DocxSession {
  doc: Document;
  win: Window;
  rendered: RenderedDocx;
  layout: DocxLayout | null;
  // Notes what is at the top of the pane (the cited passage when it is in view), to keep it there.
  remember: () => void;
  // Fits the page to the pane at the current zoom, keeping what was remembered in the same place.
  refit: () => void;
}

// The DOCX itself, rendered from its own structure (headings, paragraphs, tables, styles) inside a
// sandboxed iframe that cannot run scripts or navigate, and scrolled to the cited paragraphs. A
// paragraph is cited only when its text matches the cited line of the extracted text.
export function DocxOriginalView(props: ViewProps) {
  const { data, reference, location, related = NONE, title } = props;
  const frameRef = useRef<HTMLIFrameElement>(null);
  const latest = useRef(props);
  useLayoutEffect(() => {
    latest.current = props;
  });
  const [session, setSession] = useState<DocxSession | null>(null);
  const [zoom, setZoom] = useState(1);
  const zoomRef = useRef(zoom);
  // The element the pane was scrolled to (the passage's first mark, else its first paragraph).
  const cited = useRef<HTMLElement | null>(null);

  useEffect(() => {
    const frame = frameRef.current!;
    let cancelled = false;
    let resizes: ResizeObserver | undefined;
    const stops: (() => void)[] = [];

    (async () => {
      try {
        const doc = frame.contentDocument;
        const win = frame.contentWindow;
        if (!doc || !win) throw new Error('The document frame is not available.');
        doc.open();
        doc.write('<!doctype html><html><head><meta charset="utf-8"></head><body></body></html>');
        doc.close();
        const own = doc.createElement('style');
        own.textContent = DOCX_FRAME_STYLE;
        const styles = doc.createElement('div');
        doc.head.append(styles, own);

        const rendered = await renderDocx(data, doc.body, styles);
        if (cancelled) return;

        // Narrower than the page: reflow it to the pane. Zoomed out, the page has more room (the pane
        // holds more of it); zoomed in, it keeps the layout it had at 100% and grows, so a page that
        // fitted the pane scrolls sideways rather than being reflowed.
        const layout = measureDocxLayout(doc.body);
        let anchor: { el: Element; top: number } | null = null;
        const s: DocxSession = {
          doc,
          win,
          rendered,
          layout,
          remember: () => {
            const height = doc.documentElement.clientHeight;
            const at = cited.current;
            const box = at?.getBoundingClientRect();
            if (at && box && box.bottom > 0 && box.top < height) return void (anchor = { el: at, top: box.top });
            for (let y = 8; y < Math.min(height, 240); y += 24) {
              const el = doc.elementFromPoint?.(doc.documentElement.clientWidth / 2, y);
              if (el && el.closest('section.docx > *')) return void (anchor = { el, top: el.getBoundingClientRect().top });
            }
          },
          refit: () => {
            const keep = anchor;
            doc.documentElement.style.setProperty('--src-zoom', String(zoomRef.current));
            if (layout) fitDocxToWidth(layout, doc.documentElement, doc.documentElement.clientWidth / Math.min(zoomRef.current, 1));
            if (keep?.el.isConnected) win.scrollTo(0, Math.max(0, win.scrollY + keep.el.getBoundingClientRect().top - keep.top));
            if (cited.current && keep?.el === cited.current) revealInTable(cited.current);
          },
        };
        s.refit();

        // A table that scrolls sideways (mouse, trackpad or touch): keep its faded edges current.
        const sideways = (event: Event) => {
          const el = event.target as Element | null;
          if (el?.matches?.('table.src-docx-wide')) markTableOverflow(el as HTMLElement);
        };
        doc.addEventListener('scroll', sideways, { capture: true, passive: true });
        stops.push(() => doc.removeEventListener('scroll', sideways, { capture: true }));
        win.addEventListener('scroll', s.remember, { passive: true });
        stops.push(() => win.removeEventListener('scroll', s.remember));
        // Zoom keys and Ctrl + wheel work while the document has focus, as they do on the PDF.
        const onKey = (event: KeyboardEvent) => {
          const next = zoomForKey(zoomRef.current, event);
          if (next === null) return;
          event.preventDefault();
          setZoom(next);
        };
        doc.addEventListener('keydown', onKey);
        stops.push(() => doc.removeEventListener('keydown', onKey));
        stops.push(wheelZoom(doc, setZoom));

        // The pane resized (the window, or panels added beside it): fit the page again and keep the
        // same place in view - the cited passage when it was in view, else what was at the top.
        if (layout && typeof ResizeObserver !== 'undefined') {
          let width = doc.documentElement.clientWidth;
          resizes = new ResizeObserver(() => {
            if (doc.documentElement.clientWidth === width) return;
            width = doc.documentElement.clientWidth;
            s.refit();
          });
          resizes.observe(frame);
        }
        setSession(s);
      } catch {
        if (!cancelled) latest.current.onFailed();
      }
    })();

    return () => {
      cancelled = true;
      resizes?.disconnect();
      stops.forEach((stop) => stop());
    };
  }, [data]);

  // Zoom: the page grows or shrinks around what is in view.
  useEffect(() => {
    zoomRef.current = zoom;
    if (!session) return;
    session.remember();
    session.refit();
  }, [session, zoom]);

  useEffect(() => {
    if (!session) return;
    const { doc, win, rendered } = session;
    const { lines, onNavigated } = latest.current;

    // The previous passage's marks, bars and current place go first.
    unmarkAll(doc.body);
    doc.querySelectorAll('.src-docx-cited, .src-docx-heading').forEach((el) => el.classList.remove('src-docx-cited', 'src-docx-heading'));
    doc.querySelectorAll('[aria-current]').forEach((el) => el.removeAttribute('aria-current'));
    cited.current = null;

    // The rendered paragraphs (or table rows) that hold extracted lines [line, endLine): paragraphs
    // and whole table rows, as text is extracted now; else one paragraph per line, as sources
    // uploaded before table rows were kept together were extracted.
    const sets = [rendered.blocks, rendered.paragraphs].map((list) => ({ elements: list.map((b) => b.element), texts: list.map((b) => b.text) }));
    const align = (line: number, endLine: number): HTMLElement[] | null => {
      for (const set of sets) {
        const span = alignParagraphs(set.texts, lines, line, endLine);
        if (span) return set.elements.slice(span.first, span.last + 1);
      }
      return null;
    };
    // A target's exact text in its paragraphs: a quote's words (across paragraphs if it runs over
    // several), or the whole of a heading's paragraph - the paragraph is matched to the heading line
    // already, so its own text is the heading as Word draws it (automatic numbers aside).
    const markTarget = (target: HighlightTarget, className: string): HTMLElement[] => {
      const block = align(target.line, target.endLine);
      if (!block) return [];
      return target.kind === 'quote' ? markAcross(block, target.text, className) : markAcross([block[0]], block[0].textContent ?? '', className);
    };

    let result: NavigationResult = { status: 'not-found' };
    const target = highlightTarget(lines, reference, location);
    let at: HTMLElement | null = null;
    if (location.status === 'located') {
      // A section is cited from its heading; a quote only by its own paragraphs, with the heading of
      // its section marked apart - never the whole section above it.
      const block = align(location.line, Math.max(location.endLine, location.line + 1));
      if (block) {
        block.forEach((p) => p.classList.add('src-docx-cited'));
        const heading =
          target?.kind === 'quote' && location.headingLine !== null && location.headingLine < location.line
            ? align(location.headingLine, location.headingLine + 1)?.[0]
            : undefined;
        const before = heading && heading.compareDocumentPosition(block[0]) & Node.DOCUMENT_POSITION_FOLLOWING;
        (heading && before ? heading : block[0]).classList.add('src-docx-heading');
        const marks = target ? markTarget(target, HIT) : [];
        // The quoted words not matched in the paragraphs: the view opens at them with the bar, and
        // says so - nothing is painted yellow that is not the passage.
        at = marks[0] ?? block[0];
        at.setAttribute('aria-current', 'location');
        result = marks.length ? { status: 'highlighted' } : { status: 'paragraph-only' };
      }
    }
    for (const r of related) markTarget(r.target, RELATED_HIT);

    cited.current = at;
    if (at) {
      win.scrollTo(0, Math.max(0, at.getBoundingClientRect().top + win.scrollY - 24));
      // In a table that scrolls sideways, bring the cited cell into view too.
      revealInTable(at);
    } else {
      win.scrollTo(0, 0);
    }
    session.remember();
    onNavigated(result);
  }, [session, reference, location, related]);

  return (
    <div className="src-original-frame">
      <DocumentBar zoom={zoom} onZoom={setZoom} label="Word document" />
      {/* No allow-scripts, allow-forms, allow-popups or allow-top-navigation: the document is inert and
          cannot leave the frame. allow-same-origin only lets the viewer reach in to render and scroll it. */}
      <iframe ref={frameRef} className="src-original src-docx" sandbox="allow-same-origin" title={title} tabIndex={0} data-autofocus />
    </div>
  );
}
