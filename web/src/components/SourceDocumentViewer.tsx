import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { ApiError } from '../api/client';
import { requirementSourcesApi } from '../api/requirementSources';
import type { RequirementSource, SourceReference } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { headingTitle, locateReference, markExcerpt, originalKind, sourceFormatNote, sourceLines, type ReferenceLocation } from '../lib/sourceReferences';
import { DocxOriginalView, PdfOriginalView, type NavigationResult } from './OriginalDocumentViews';
import { Dialog } from './ui/Dialog';
import { ErrorState } from './ui/ErrorState';

interface Props {
  reference: SourceReference;
  // All the passages of the question the reference is one of (both statements of a conflict): the
  // viewer steps between them, and the extracted text marks the others too.
  passages?: SourceReference[];
  // What the highlighted quote is to the question ("Conflicting statement"); "Quoted evidence" if unset.
  purpose?: string;
  // The whole source, with nothing highlighted: no passage of it could be verified for the question.
  browse?: boolean;
  onClose: () => void;
}

type ViewerState = { status: 'loading' } | { status: 'ready'; source: RequirementSource } | { status: 'error'; code: number };

// The original document: fetched once the source says one is kept; null data while loading.
type OriginalState =
  | { status: 'none' } // no original kept (pasted, TXT, uploaded before originals were kept)
  | { status: 'loading' }
  | { status: 'ready'; data: ArrayBuffer }
  | { status: 'failed'; reason: 'unavailable' | 'render' | 'network' };

// The requirement source a clarification question cites, opened over the current page at the cited
// section. Everything is fetched through the authorized source endpoints (project membership and S2 are
// enforced there), never from a URL in the reference. When the uploaded PDF/DOCX itself is kept, that
// document is shown, at the cited page or paragraph with the heading or quote marked; otherwise - or if
// it cannot be shown - the extracted text is, and the viewer says which it is showing and why. The page
// underneath keeps its route and scroll position, and Dialog returns focus to the link that opened it.
export function SourceDocumentViewer({ reference: opened, passages, purpose, browse = false, onClose }: Props) {
  const { token, hasRole } = useAuth();
  const [state, setState] = useState<ViewerState>({ status: 'loading' });
  const [original, setOriginal] = useState<OriginalState>({ status: 'none' });
  const [showText, setShowText] = useState(false);
  const [navigation, setNavigation] = useState<NavigationResult | null>(null);
  const [attempt, setAttempt] = useState(0);
  const steps = passages && passages.length > 1 && passages.includes(opened) ? passages : null;
  const [step, setStep] = useState(() => (steps ? steps.indexOf(opened) : 0));
  const reference = steps ? steps[step] : opened;

  useEffect(() => {
    let cancelled = false;
    const abort = new AbortController();
    requirementSourcesApi
      .getById(reference.sourceId, token)
      .then(async (source) => {
        if (cancelled) return;
        setState({ status: 'ready', source });
        if (!originalKind(source)) return setOriginal({ status: 'none' });
        setOriginal({ status: 'loading' });
        try {
          const data = await requirementSourcesApi.getOriginal(reference.sourceId, token, abort.signal);
          if (!cancelled) setOriginal({ status: 'ready', data });
        } catch (e) {
          if (!cancelled) setOriginal({ status: 'failed', reason: e instanceof ApiError ? 'unavailable' : 'network' });
        }
      })
      .catch((e: unknown) => !cancelled && setState({ status: 'error', code: e instanceof ApiError ? e.status : 0 }));
    return () => {
      cancelled = true;
      abort.abort();
    };
  }, [reference.sourceId, token, attempt]);

  const source = state.status === 'ready' ? state.source : null;
  const lines = useMemo(() => (source ? sourceLines(source.rawText) : []), [source]);
  const location = useMemo<ReferenceLocation | null>(() => (source ? locateReference(lines, reference) : null), [source, lines, reference]);
  const others = useMemo(
    () => (source && steps ? steps.filter((p) => p !== reference).map((p) => locateReference(lines, p)) : []),
    [source, lines, steps, reference],
  );
  const located = location?.status === 'located' ? location : null;
  const kind = source ? originalKind(source) : null;
  const canAttach = !!source && !kind && WRITER_ROLES.some(hasRole) && /\.(pdf|docx)$/i.test(source.originalFileName ?? '');
  const showingOriginal = !!kind && !showText && (original.status === 'loading' || original.status === 'ready');

  const retry = () => {
    setState({ status: 'loading' });
    setOriginal({ status: 'none' });
    setNavigation(null);
    setAttempt((n) => n + 1);
  };

  const heading = reference.heading ? headingTitle(reference.heading) : null;
  const where = browse ? 'the clarification question' : heading && heading !== reference.label ? `${reference.label} — ${heading}` : reference.label;
  const goTo = (n: number) => {
    setNavigation(null);
    setStep(n);
  };
  const fileName = source?.originalFileName || 'Requirement source';

  return (
    <Dialog
      title={fileName}
      description={`Opened from “${where}”`}
      onClose={onClose}
      width={1040}
      footer={
        <>
          {kind && original.status === 'ready' && (
            <button type="button" className="btn btn-ghost" onClick={() => setShowText((v) => !v)} aria-pressed={showText}>
              {showText ? `Show the original ${kind.toUpperCase()}` : 'Show the extracted text'}
            </button>
          )}
          <button type="button" className="btn btn-secondary" onClick={onClose}>
            Close
          </button>
        </>
      }
    >
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
          onRetry={state.code === 403 || state.code === 404 ? undefined : retry}
        />
      )}

      {source && location && (
        <div className="src-viewer">
          <p className="src-viewer-note">
            {showingOriginal ? `The original ${kind!.toUpperCase()} uploaded as ${fileName}.` : sourceFormatNote(source.originalFileName)}
            {!showingOriginal && kind && <> {fallbackReason(original, showText)}</>}
          </p>

          {canAttach && <AttachOriginal source={source} token={token} onAttached={retry} />}

          {steps && (
            <div className="src-viewer-steps" role="group" aria-label="Passages of this question">
              {steps.map((p, i) => (
                <button
                  key={i}
                  type="button"
                  className={`btn btn-sm ${i === step ? 'btn-secondary' : 'btn-ghost'}`}
                  aria-pressed={i === step}
                  onClick={() => goTo(i)}
                >
                  {`Passage ${i + 1} of ${steps.length}`}
                  {p.sectionNumber ? ` (§${p.sectionNumber})` : ` (line ${p.line + 1})`}
                </button>
              ))}
            </div>
          )}

          {browse ? (
            <p className="src-viewer-warning" role="status">
              No passage of this source is linked to the question, so nothing is highlighted. The whole source is shown.
            </p>
          ) : located ? (
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

          {showingOriginal && located && !browse && <NavigationNote navigation={navigation} reference={reference} />}

          {showingOriginal && original.status === 'loading' && (
            <p className="src-viewer-status" role="status">
              Loading the original document…
            </p>
          )}

          {showingOriginal && original.status === 'ready' && kind === 'pdf' && (
            <PdfOriginalView
              key={step}
              data={original.data}
              lines={lines}
              reference={reference}
              location={location}
              pageLineStarts={source.originalDocument?.pageLineStarts}
              title={`${fileName}, original PDF`}
              onNavigated={setNavigation}
              onFailed={() => setOriginal({ status: 'failed', reason: 'render' })}
            />
          )}
          {showingOriginal && original.status === 'ready' && kind === 'docx' && (
            <DocxOriginalView
              key={step}
              data={original.data}
              lines={lines}
              reference={reference}
              location={location}
              title={`${fileName}, original Word document`}
              onNavigated={setNavigation}
              onFailed={() => setOriginal({ status: 'failed', reason: 'render' })}
            />
          )}

          {!showingOriginal && <ExtractedTextView lines={lines} reference={reference} location={location} others={others} />}
        </div>
      )}
    </Dialog>
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
