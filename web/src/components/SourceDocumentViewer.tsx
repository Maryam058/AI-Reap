import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { ApiError } from '../api/client';
import { requirementSourcesApi } from '../api/requirementSources';
import type { RequirementSource, SourceReference } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import {
  headingTitle,
  locateReference,
  markExcerpt,
  originalKind,
  sourceFormatNote,
  sourceLines,
  sourceLinkLabel,
  type ReferenceLocation,
} from '../lib/sourceReferences';
import { IconChevronLeft, IconChevronRight, IconPlus, IconX } from './icons';
import { DocxOriginalView, PdfOriginalView, type NavigationResult } from './OriginalDocumentViews';
import { Dialog } from './ui/Dialog';
import { ErrorState } from './ui/ErrorState';

interface Props {
  reference: SourceReference;
  // All the passages of the question the reference is one of (both statements of a conflict): the
  // viewer steps between them, can show several side by side, and the extracted text marks the others too.
  passages?: SourceReference[];
  // What the highlighted quote is to the question ("Conflicting statement"); "Quoted evidence" if unset.
  purpose?: string;
  // The clarification question the passages belong to, shown above the statement (or above all the
  // panels) so the reader sees what is being asked while reading the source. It stays the same while
  // stepping between, adding or removing passages.
  question?: string;
  onClose: () => void;
}

type ViewerState = { status: 'loading' } | { status: 'ready'; source: RequirementSource } | { status: 'error'; code: number };

// The original document: fetched once the source says one is kept; null data while loading.
type OriginalState =
  | { status: 'none' } // no original kept (pasted, TXT, uploaded before originals were kept)
  | { status: 'loading' }
  | { status: 'ready'; data: ArrayBuffer }
  | { status: 'failed'; reason: 'unavailable' | 'render' | 'network' };

// One requirement source as loaded for this viewer. Panels showing passages of the same source share it,
// so a document is fetched once however many of its passages are open. generation counts the loads, so a
// panel starts afresh (navigation note included) after a retry.
interface SourceEntry {
  state: ViewerState;
  original: OriginalState;
  generation: number;
}

// One panel of the viewer: which of the passages it shows, and whether it shows the extracted text in
// place of the original document. id is stable for the life of the panel.
interface Panel {
  id: number;
  index: number;
  showText: boolean;
}

const LOADING: SourceEntry = { state: { status: 'loading' }, original: { status: 'none' }, generation: 0 };

// Loads each requirement source once (and its original document, when one is kept), through the
// authorized source endpoints, for as long as the viewer is open. retry reloads one source.
function useRequirementSources(sourceIds: string[], token: string | null) {
  const [entries, setEntries] = useState<Record<string, SourceEntry>>({});
  const loads = useRef(new Map<string, AbortController>());

  const load = useCallback(
    (id: string) => {
      loads.current.get(id)?.abort();
      const abort = new AbortController();
      loads.current.set(id, abort);
      const set = (patch: Partial<SourceEntry>) =>
        !abort.signal.aborted && setEntries((all) => ({ ...all, [id]: { ...(all[id] ?? LOADING), ...patch } }));
      setEntries((all) => ({ ...all, [id]: { ...LOADING, generation: (all[id]?.generation ?? 0) + 1 } }));

      requirementSourcesApi
        .getById(id, token)
        .then(async (source) => {
          if (abort.signal.aborted) return;
          if (!originalKind(source)) return set({ state: { status: 'ready', source }, original: { status: 'none' } });
          set({ state: { status: 'ready', source }, original: { status: 'loading' } });
          try {
            const data = await requirementSourcesApi.getOriginal(id, token, abort.signal);
            set({ original: { status: 'ready', data } });
          } catch (e) {
            set({ original: { status: 'failed', reason: e instanceof ApiError ? 'unavailable' : 'network' } });
          }
        })
        .catch((e: unknown) => set({ state: { status: 'error', code: e instanceof ApiError ? e.status : 0 } }));
    },
    [token],
  );

  // Cancels every load on close (or when the caller's token changes, so the next effect reloads).
  useEffect(() => {
    const current = loads.current;
    return () => {
      for (const abort of current.values()) abort.abort();
      current.clear();
    };
  }, [token]);

  const wanted = [...new Set(sourceIds)].join('\n');
  useEffect(() => {
    for (const id of wanted.split('\n')) if (id && !loads.current.has(id)) load(id);
  }, [wanted, load]);

  const renderFailed = useCallback(
    (id: string) => setEntries((all) => (all[id] ? { ...all, [id]: { ...all[id], original: { status: 'failed', reason: 'render' } } } : all)),
    [],
  );

  return { entries, retry: load, renderFailed };
}

// The requirement source a clarification question cites, opened over the current page at the cited
// section. Everything is fetched through the authorized source endpoints (project membership and S2 are
// enforced there), never from a URL in the reference. When the uploaded PDF/DOCX itself is kept, that
// document is shown, at the cited page or paragraph with the heading or quote marked; otherwise - or if
// it cannot be shown - the extracted text is, and the viewer says which it is showing and why. The page
// underneath keeps its route and scroll position, and Dialog returns focus to the link that opened it.
//
// A question with several passages opens on the one chosen, with tabs to step between them; "Add
// passage" puts further passages side by side, each panel with its own document, highlight, scroll
// position and passage, which can be replaced, moved or removed without touching the others. A passage
// is shown in one panel at a time, and the question stays above all of them.
export function SourceDocumentViewer({ reference: opened, passages, purpose, question, onClose }: Props) {
  const { token, hasRole } = useAuth();
  const list = useMemo(() => (passages && passages.length > 1 && passages.includes(opened) ? passages : [opened]), [passages, opened]);
  const steps = list.length > 1 ? list : null;
  const nextId = useRef(1);
  const [panels, setPanels] = useState<Panel[]>(() => [{ id: 0, index: Math.max(0, list.indexOf(opened)), showText: false }]);
  const { entries, retry, renderFailed } = useRequirementSources(
    panels.map((p) => list[p.index].sourceId),
    token,
  );
  const canWrite = WRITER_ROLES.some(hasRole);
  const single = panels.length === 1;
  const shown = new Set(panels.map((p) => p.index));
  const firstUnused = list.findIndex((_, i) => !shown.has(i));

  const update = (id: number, patch: Partial<Panel>) => setPanels((all) => all.map((p) => (p.id === id ? { ...p, ...patch } : p)));
  const addPanel = () => {
    if (firstUnused < 0) return;
    setPanels((all) => [...all, { id: nextId.current++, index: firstUnused, showText: false }]);
  };
  const removePanel = (id: number) => setPanels((all) => (all.length > 1 ? all.filter((p) => p.id !== id) : all));
  const movePanel = (from: number, to: number) =>
    setPanels((all) => {
      if (to < 0 || to >= all.length) return all;
      const next = [...all];
      const [moved] = next.splice(from, 1);
      next.splice(to, 0, moved);
      return next;
    });

  const others = (reference: SourceReference) => (steps ? steps.filter((p) => p !== reference) : []);
  const addButton = steps && (
    <button type="button" className="btn btn-sm btn-secondary src-viewer-add" onClick={addPanel} disabled={firstUnused < 0} aria-label="Add a passage panel">
      <IconPlus width={14} height={14} aria-hidden="true" />
      Add passage
    </button>
  );
  const questionBlock = question?.trim() && (
    <section className="src-viewer-question" aria-label="Clarification question">
      <span className="detail-label">Clarification question</span>
      <p>{question}</p>
    </section>
  );

  if (single) {
    const panel = panels[0];
    const reference = list[panel.index];
    const entry = entries[reference.sourceId] ?? LOADING;
    const source = entry.state.status === 'ready' ? entry.state.source : null;
    const kind = source ? originalKind(source) : null;
    const fileName = source?.originalFileName || 'Requirement source';
    return (
      <Dialog
        title={fileName}
        description={`Opened from “${whereOf(reference)}”`}
        onClose={onClose}
        width={1040}
        footer={
          <>
            {kind && entry.original.status === 'ready' && (
              <button type="button" className="btn btn-ghost" onClick={() => update(panel.id, { showText: !panel.showText })} aria-pressed={panel.showText}>
                {panel.showText ? `Show the original ${kind.toUpperCase()}` : 'Show the extracted text'}
              </button>
            )}
            <button type="button" className="btn btn-secondary" onClick={onClose}>
              Close
            </button>
          </>
        }
      >
        <SourcePanel
          key={`${panel.index}:${entry.generation}`}
          entry={entry}
          reference={reference}
          others={others(reference)}
          purpose={purpose}
          showText={panel.showText}
          canWrite={canWrite}
          token={token}
          onRetry={() => retry(reference.sourceId)}
          onRenderFailed={() => renderFailed(reference.sourceId)}
          lead={
            <>
              {steps && (
                <div className="src-viewer-toolbar">
                  <div className="src-viewer-steps" role="group" aria-label="Passages of this question">
                    {steps.map((p, i) => (
                      <button
                        key={i}
                        type="button"
                        className={`btn btn-sm ${i === panel.index ? 'btn-secondary' : 'btn-ghost'}`}
                        aria-pressed={i === panel.index}
                        onClick={() => update(panel.id, { index: i })}
                      >
                        {`Passage ${i + 1} of ${steps.length}`}
                        {p.heading ? ` (${sourceLinkLabel(p)})` : ''}
                      </button>
                    ))}
                  </div>
                  {addButton}
                </div>
              )}
              {questionBlock}
            </>
          }
        />
      </Dialog>
    );
  }

  // Several panels: the title names the document when they all show the same one.
  const files = new Set(
    panels.map((p) => {
      const state = entries[list[p.index].sourceId]?.state;
      return state?.status === 'ready' ? state.source.originalFileName || 'Requirement source' : null;
    }),
  );
  const [onlyFile] = files.size === 1 ? [...files] : [null];
  return (
    <Dialog
      title={onlyFile ?? 'Compare source passages'}
      description={`${panels.length} of ${list.length} passages side by side`}
      onClose={onClose}
      width={1560}
      footer={
        <button type="button" className="btn btn-secondary" onClick={onClose}>
          Close
        </button>
      }
    >
      <div className="src-viewer src-viewer-compare">
        <div className="src-viewer-toolbar">
          <p className="src-viewer-note">Each panel scrolls on its own. Choose the passage a panel shows, move it, or remove it.</p>
          {addButton}
        </div>
        {questionBlock}
        <div className="src-viewer-panels" role="list" aria-label="Passages side by side">
          {panels.map((panel, position) => {
            const reference = list[panel.index];
            const entry = entries[reference.sourceId] ?? LOADING;
            const source = entry.state.status === 'ready' ? entry.state.source : null;
            const kind = source ? originalKind(source) : null;
            const n = position + 1;
            return (
              <section key={`${panel.id}:${position}:${panel.index}:${entry.generation}`} className="src-panel" role="listitem" aria-label={`Panel ${n}: Passage ${panel.index + 1} of ${list.length}`}>
                <div className="src-panel-head">
                  <select
                    className="select-input"
                    value={panel.index}
                    onChange={(e) => update(panel.id, { index: Number(e.target.value) })}
                    aria-label={`Passage shown in panel ${n}`}
                    title={passageTitle(reference, panel.index, list.length)}
                  >
                    {list.map((p, i) => (
                      <option key={i} value={i} disabled={i !== panel.index && shown.has(i)} title={passageTitle(p, i, list.length)}>
                        {passageOption(p, i, list.length)}
                        {i !== panel.index && shown.has(i) ? ' – already shown' : ''}
                      </option>
                    ))}
                  </select>
                  <div className="src-panel-actions">
                    <button type="button" className="icon-btn" onClick={() => movePanel(position, position - 1)} disabled={position === 0} aria-label={`Move panel ${n} left`}>
                      <IconChevronLeft width={16} height={16} aria-hidden="true" />
                    </button>
                    <button
                      type="button"
                      className="icon-btn"
                      onClick={() => movePanel(position, position + 1)}
                      disabled={position === panels.length - 1}
                      aria-label={`Move panel ${n} right`}
                    >
                      <IconChevronRight width={16} height={16} aria-hidden="true" />
                    </button>
                    <button type="button" className="icon-btn" onClick={() => removePanel(panel.id)} aria-label={`Remove panel ${n}`}>
                      <IconX width={16} height={16} aria-hidden="true" />
                    </button>
                  </div>
                </div>
                <SourcePanel
                  entry={entry}
                  reference={reference}
                  others={others(reference)}
                  purpose={purpose}
                  showText={panel.showText}
                  canWrite={canWrite}
                  token={token}
                  onRetry={() => retry(reference.sourceId)}
                  onRenderFailed={() => renderFailed(reference.sourceId)}
                  compact
                  lead={
                    <div className="src-panel-where">
                      <p className="src-panel-title" title={`${source?.originalFileName || 'Requirement source'} · ${whereOf(reference)}`}>
                        {/* The dialog title already names the file when every panel shows the same one. */}
                        {!onlyFile && <span className="src-panel-file">{source?.originalFileName || 'Requirement source'}</span>}
                        <span className="src-panel-ref">{whereOf(reference)}</span>
                      </p>
                      {kind && entry.original.status === 'ready' && (
                        <button
                          type="button"
                          className="btn btn-ghost btn-sm"
                          onClick={() => update(panel.id, { showText: !panel.showText })}
                          aria-pressed={panel.showText}
                          aria-label={`${panel.showText ? `Show the original ${kind.toUpperCase()}` : 'Show the extracted text'} in panel ${n}`}
                        >
                          {panel.showText ? `Show the original ${kind.toUpperCase()}` : 'Show the extracted text'}
                        </button>
                      )}
                    </div>
                  }
                />
              </section>
            );
          })}
        </div>
      </div>
    </Dialog>
  );
}

// The passage picker: a short option ("2 of 5 · 12.7 Decline Categories"), its full name as a tooltip.
const OPTION_NAME = 40;
function passageName(reference: SourceReference): string {
  return (reference.heading ? sourceLinkLabel(reference) : reference.label).replace(/\s+/g, ' ').trim() || 'Source passage';
}
function passageOption(reference: SourceReference, i: number, count: number): string {
  const name = passageName(reference);
  return `${i + 1} of ${count} · ${name.length > OPTION_NAME ? `${name.slice(0, OPTION_NAME - 1).trimEnd()}…` : name}`;
}
function passageTitle(reference: SourceReference, i: number, count: number): string {
  return `Passage ${i + 1} of ${count}: ${passageName(reference)}`;
}

// A panel's header names its file, so its note says what is shown without naming the file again.
function panelFormatNote(fileName: string | null | undefined): string {
  switch (fileName?.toLowerCase().match(/\.([a-z0-9]+)$/)?.[1]) {
    case 'pdf':
      return 'Extracted text only: the PDF’s pages, images and layout are not kept.';
    case 'docx':
      return 'Extracted paragraph text only: tables, headers, footers and images are not kept.';
    case 'txt':
      return 'The file as uploaded.';
    case undefined:
      return 'The requirement text as it was entered.';
    default:
      return 'Text extracted when the file was uploaded.';
  }
}

// A passage is named by its section; a section link by its own words (and heading, when they differ).
function whereOf(reference: SourceReference): string {
  const heading = reference.heading ? headingTitle(reference.heading) : null;
  return reference.excerptIsQuote ? sourceLinkLabel(reference) : heading && heading !== reference.label ? `${reference.label} — ${heading}` : reference.label;
}

// One passage of a source: loading and error states, which form of the document is shown and why, the
// quoted statement, and the document itself at the cited place. lead is shown above the statement once
// the source is loaded (the passage tabs and question in the single view; the panel's file in a panel).
function SourcePanel({
  entry,
  reference,
  others: otherPassages,
  purpose,
  showText,
  canWrite,
  token,
  lead,
  compact = false,
  onRetry,
  onRenderFailed,
}: {
  entry: SourceEntry;
  reference: SourceReference;
  others: SourceReference[];
  purpose?: string;
  showText: boolean;
  canWrite: boolean;
  token: string | null;
  lead?: ReactNode;
  /** In a panel: the lead names the file, so the note does not repeat it (and is left out for the original). */
  compact?: boolean;
  onRetry: () => void;
  onRenderFailed: () => void;
}) {
  const [navigation, setNavigation] = useState<NavigationResult | null>(null);
  const { state, original } = entry;
  const source = state.status === 'ready' ? state.source : null;
  const lines = useMemo(() => (source ? sourceLines(source.rawText) : []), [source]);
  const location = useMemo<ReferenceLocation | null>(() => (source ? locateReference(lines, reference) : null), [source, lines, reference]);
  const others = useMemo(() => (source ? otherPassages.map((p) => locateReference(lines, p)) : []), [source, lines, otherPassages]);
  const located = location?.status === 'located' ? location : null;
  const kind = source ? originalKind(source) : null;
  const canAttach = !!source && !kind && canWrite && /\.(pdf|docx)$/i.test(source.originalFileName ?? '');
  const showingOriginal = !!kind && !showText && (original.status === 'loading' || original.status === 'ready');
  const fileName = source?.originalFileName || 'Requirement source';

  return (
    <>
      {state.status === 'loading' && (
        <p className="src-viewer-status" role="status">
          Loading the requirement source…
        </p>
      )}

      {state.status === 'error' && (
        <ErrorState
          title={state.code === 403 ? 'You can’t open this source' : state.code === 404 ? 'Source not available' : 'Couldn’t load the source'}
          description={
            state.code === 403
              ? 'Your role or project membership does not allow reading the raw requirement source.'
              : state.code === 404
                ? 'This requirement source no longer exists.'
                : 'The requirement source could not be loaded. Check your connection and try again.'
          }
          onRetry={state.code === 403 || state.code === 404 ? undefined : onRetry}
        />
      )}

      {source && location && (
        <div className="src-viewer">
          {!(compact && showingOriginal) && (
            <p className="src-viewer-note">
              {showingOriginal
                ? `The original ${kind!.toUpperCase()} uploaded as ${fileName}.`
                : compact
                  ? panelFormatNote(source.originalFileName)
                  : sourceFormatNote(source.originalFileName)}
              {!showingOriginal && kind && <> {fallbackReason(original, showText)}</>}
            </p>
          )}

          {canAttach && <AttachOriginal source={source} token={token} onAttached={onRetry} />}

          {lead}

          {located ? (
            (located.excerpt ?? reference.excerpt) && (
              <blockquote className="src-viewer-evidence">
                <span className="detail-label">{reference.excerptIsQuote ? (purpose ?? 'Quoted evidence') : 'Section text'}</span>
                {located.excerpt ?? reference.excerpt}
              </blockquote>
            )
          ) : (
            <p className="src-viewer-warning" role="status">
              “{reference.label}” could not be located in this source, so nothing is highlighted.
            </p>
          )}

          {showingOriginal && located && <NavigationNote navigation={navigation} reference={reference} />}

          {showingOriginal && original.status === 'loading' && (
            <p className="src-viewer-status" role="status">
              Loading the original document…
            </p>
          )}

          {showingOriginal && original.status === 'ready' && kind === 'pdf' && (
            <PdfOriginalView
              data={original.data}
              lines={lines}
              reference={reference}
              location={location}
              pageLineStarts={source.originalDocument?.pageLineStarts}
              title={`${fileName}, original PDF`}
              onNavigated={setNavigation}
              onFailed={onRenderFailed}
            />
          )}
          {showingOriginal && original.status === 'ready' && kind === 'docx' && (
            <DocxOriginalView
              data={original.data}
              lines={lines}
              reference={reference}
              location={location}
              title={`${fileName}, original Word document`}
              onNavigated={setNavigation}
              onFailed={onRenderFailed}
            />
          )}

          {!showingOriginal && <ExtractedTextView lines={lines} reference={reference} location={location} others={others} />}
        </div>
      )}
    </>
  );
}


const WRITER_ROLES = ['Administrator', 'BusinessAnalyst'];

// For a PDF/DOCX source uploaded before originals were kept: the same file can be attached, and the
// viewer then shows it. The server accepts it only when its text is the source's text, so a different
// document or version is refused and every link into the source still lands where it did.
function AttachOriginal({ source, token, onAttached }: { source: RequirementSource; token: string | null; onAttached: () => void }) {
  const input = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const extension = source.originalFileName!.slice(source.originalFileName!.lastIndexOf('.')).toLowerCase();

  const attach = async (file: File | undefined) => {
    if (!file) return;
    setBusy(true);
    setError(null);
    try {
      await requirementSourcesApi.attachOriginal(source.id, file, token);
      onAttached();
    } catch (e) {
      setError(
        e instanceof ApiError && e.status === 422
          ? `${file.name} is not the document that was uploaded: its text differs. Choose the same version of ${source.originalFileName}.`
          : e instanceof ApiError && e.status === 409
            ? 'An original can no longer be attached to this source.'
            : e instanceof ApiError && e.status === 400
              ? problemDetail(e.message) ?? 'This file could not be read.'
              : 'The file could not be attached. Check your connection and try again.',
      );
      setBusy(false);
    } finally {
      if (input.current) input.current.value = '';
    }
  };

  return (
    <div className="src-viewer-attach">
      <p>
        Only the extracted text of this source is kept: it was uploaded before original files were stored. Attach{' '}
        <strong>{source.originalFileName}</strong> again to view the document itself, opened at the cited section.
      </p>
      <input ref={input} type="file" accept={extension} hidden onChange={(e) => void attach(e.target.files?.[0])} />
      <button type="button" className="btn btn-secondary" disabled={busy} onClick={() => input.current?.click()}>
        {busy ? 'Attaching…' : `Attach the original ${extension.slice(1).toUpperCase()}`}
      </button>
      {error && (
        <p className="src-viewer-warning" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}

// A 400's message: plain text from BadRequest("..."), or a problem document's detail.
function problemDetail(body: string): string | null {
  try {
    const parsed = JSON.parse(body) as { detail?: string; title?: string };
    return parsed.detail ?? parsed.title ?? null;
  } catch {
    return body.trim() || null;
  }
}

function fallbackReason(original: OriginalState, chosen: boolean): string {
  if (chosen) return 'You are viewing the text extracted from it.';
  if (original.status !== 'failed') return '';
  if (original.reason === 'render') return 'The original could not be displayed, so its extracted text is shown.';
  if (original.reason === 'network') return 'The original could not be loaded, so its extracted text is shown.';
  return 'The original file is no longer available, so its extracted text is shown.';
}

function NavigationNote({ navigation, reference }: { navigation: NavigationResult | null; reference: SourceReference }) {
  if (!navigation) return null;
  const text =
    navigation.status === 'highlighted'
      ? `${navigation.page ? `Page ${navigation.page}: ` : ''}the cited ${reference.excerptIsQuote ? 'passage' : 'section'} is highlighted.`
      : navigation.status === 'page-only'
        ? `Opened at page ${navigation.page}, where “${reference.label}” is. Its text could not be matched on the page, so it is not highlighted.`
        : `“${reference.label}” could not be matched in the original document, so it opens at the start and nothing is highlighted. “Show the extracted text” shows the cited section.`;
  return (
    <p className={navigation.status === 'highlighted' ? 'src-viewer-status' : 'src-viewer-warning'} role="status">
      {text}
    </p>
  );
}

// The text extracted at upload, as numbered lines with the cited range marked: for sources without a
// kept original, and as the fallback when the original cannot be shown.
// others: where the question's other passages are - marked too, more lightly, so a conflict's two
// statements are both visible.
function ExtractedTextView({
  lines,
  reference,
  location,
  others = [],
}: {
  lines: string[];
  reference: SourceReference;
  location: ReferenceLocation;
  others?: ReferenceLocation[];
}) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const targetRef = useRef<HTMLDivElement>(null);
  const located = location.status === 'located' ? location : null;

  // Scroll only the document pane, so the page behind the dialog never moves.
  useLayoutEffect(() => {
    const pane = scrollRef.current;
    const target = targetRef.current;
    if (pane && target) pane.scrollTop = Math.max(0, target.offsetTop - 24);
  }, [located]);

  return (
    <div className="src-viewer-doc" ref={scrollRef} tabIndex={0} data-autofocus role="document" aria-label={`Requirement source text, ${lines.length} lines`}>
      {lines.map((text, i) => {
        const inRange = !!located && i >= located.line && i < located.endLine;
        const isHeading = !!located && i === located.headingLine;
        const isOther = !inRange && others.some((o) => o.status === 'located' && i >= o.line && i < o.endLine);
        const marked = inRange && reference.excerptIsQuote ? markExcerpt(text, reference.excerpt) : null;
        const className = ['src-line', inRange && 'src-line-cited', isOther && 'src-line-related', isHeading && 'src-line-heading']
          .filter(Boolean)
          .join(' ');
        return (
          <div
            key={i}
            className={className}
            data-line={i}
            ref={located && i === located.line ? targetRef : undefined}
            aria-current={located && i === located.line ? 'location' : undefined}
          >
            <span className="src-line-no" aria-hidden="true">
              {i + 1}
            </span>
            <span className="src-line-text">
              {marked ? (
                <>
                  {marked[0]}
                  <mark>{marked[1]}</mark>
                  {marked[2]}
                </>
              ) : (
                text || ' '
              )}
            </span>
          </div>
        );
      })}
    </div>
  );
}
