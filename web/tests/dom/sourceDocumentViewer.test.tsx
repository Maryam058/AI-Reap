// Opening a source passage (ClarificationQuestionCard -> SourceDocumentViewer): the modal opens over
// the same route, fetches through the (mocked) authorized API, shows the original PDF at the cited page
// or the original DOCX at the cited paragraphs with the heading or quote marked, falls back to the
// extracted text with a reason, and on close returns focus to the link without moving the page.
// The document renderers are replaced by fakes that build the text layer / paragraphs from the source
// text, so what is tested is the navigation and highlighting, not pdf.js or docx-preview themselves.
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest';
import type { ArtifactSummary, RequirementSource, SourceReference } from '../../src/api/types';
import { ApiError } from '../../src/api/client';

const SOURCE_ID = '6a1b5f0e-6a77-4c1f-9b55-0d7c7e3b2a11';
const LINES = [
  'Triage Assessment Module', // page 1
  '1 Billing',
  'Scope',
  'Invoices are sent monthly.',
  '3.2 User Roles', // page 2
  'Nurses and doctors use the module within the triage scope. Administrators manage access permissions.',
  '2 Security',
  'Scope',
  'Passwords are stored hashed.',
  '12.7 Decline Categories', // page 3
  'The eight decline categories are listed in the decline popup.',
];
const PAGE_STARTS = [0, 4, 9];

const api = vi.hoisted(() => ({ getById: vi.fn(), getOriginal: vi.fn(), attachOriginal: vi.fn() }));
const auth = vi.hoisted(() => ({ roles: [] as string[] }));
vi.mock('../../src/api/requirementSources', () => ({ requirementSourcesApi: api }));
vi.mock('../../src/auth/AuthContext', () => ({ useAuth: () => ({ token: 'test-token', hasRole: (r: string) => auth.roles.includes(r) }) }));

const renderers = vi.hoisted(() => ({
  failPdf: false,
  pdfRendered: [] as number[],
  docxParagraphs: null as string[] | null,
  pdfOpened: 0,
  docxRendered: 0,
  pdfScales: [] as number[],
}));
vi.mock('../../src/lib/documentRenderers', () => ({
  // Each page's text layer holds that page's lines as spans, like pdf.js's.
  openPdf: async () => {
    renderers.pdfOpened++;
    if (renderers.failPdf) throw new Error('bad pdf');
    return {
      pageCount: PAGE_STARTS.length,
      pageSize: async () => ({ width: 600, height: 800 }),
      renderPage: async (n: number, container: HTMLElement, scale: number) => {
        renderers.pdfRendered.push(n);
        renderers.pdfScales.push(scale);
        const layer = document.createElement('div');
        layer.className = 'textLayer';
        for (const line of LINES.slice(PAGE_STARTS[n - 1], PAGE_STARTS[n] ?? LINES.length)) {
          const span = document.createElement('span');
          span.textContent = line;
          layer.append(span, document.createElement('br'));
        }
        container.replaceChildren(layer);
        return layer;
      },
      destroy: () => {},
    };
  },
  // One paragraph per line, after a cover paragraph the extracted text does not have.
  renderDocx: async (_data: ArrayBuffer, body: HTMLElement) => {
    renderers.docxRendered++;
    const article = body.ownerDocument.createElement('article');
    for (const line of renderers.docxParagraphs ?? ['Cover', ...LINES]) {
      const p = body.ownerDocument.createElement('p');
      p.textContent = line;
      article.append(p);
    }
    body.replaceChildren(article);
    const paragraphs = Array.from(article.querySelectorAll('p')).map((element) => ({ element, text: element.textContent ?? '' }));
    return { blocks: paragraphs, paragraphs };
  },
  // jsdom has no layout, so no page is ever narrower than its pane (docxReflow.test.tsx covers the fit).
  measureDocxLayout: () => null,
  fitDocxToWidth: () => false,
  markTableOverflow: () => {},
  revealInTable: () => {},
}));

import { useState } from 'react';
import { ClarificationQuestionCard } from '../../src/components/ClarificationQuestionCard';
import { SourceDocumentViewer } from '../../src/components/SourceDocumentViewer';
import { referenceAriaLabel, sourceLinkLabel } from '../../src/lib/sourceReferences';

const ref = (over: Partial<SourceReference> = {}): SourceReference => ({
  label: 'Section 12.7',
  start: 0,
  sourceId: SOURCE_ID,
  sectionNumber: '12.7',
  heading: '12.7 Decline Categories',
  line: 9,
  endLine: 11,
  anchor: 'L9',
  excerpt: 'The eight decline categories are listed in the decline popup.',
  excerptIsQuote: false,
  ...over,
});

function source(format: 'pdf' | 'docx' | null): RequirementSource {
  return {
    id: SOURCE_ID,
    projectId: 'p1',
    sourceType: 1,
    rawText: LINES.join('\r\n'),
    originalFileName: format ? `Triage SRS.${format}` : 'Triage SRS.pdf',
    createdAt: '2026-10-01T00:00:00Z',
    originalDocument: format ? { format, length: 1000, pageLineStarts: format === 'pdf' ? PAGE_STARTS : null, storedAt: '2026-10-01T00:00:00Z' } : null,
  } as RequirementSource;
}

// The viewer opened on a section reference from a link (the card itself links only verified passages,
// tested below): what is tested here is the viewer's navigation, highlighting and fallbacks.
function SectionLinks({ references }: { references: SourceReference[] }) {
  const [opened, setOpened] = useState<SourceReference | null>(null);
  return (
    <>
      {references.map((r, i) => (
        <button key={i} type="button" aria-label={referenceAriaLabel(r)} onClick={() => setOpened(r)}>
          {r.label}
        </button>
      ))}
      {opened && <SourceDocumentViewer reference={opened} onClose={() => setOpened(null)} />}
    </>
  );
}

function card(_reason: string, references: SourceReference[]) {
  return render(
    <div>
      <SectionLinks references={references} />
    </div>,
  );
}

const reason = 'The categories are only named in Section 12.7, and §3.2 does not say who may decline.';
const twelve = ref({ start: reason.indexOf('Section 12.7') });
const threeTwo = ref({ label: '§3.2', start: reason.indexOf('§3.2'), sectionNumber: '3.2', heading: '3.2 User Roles', line: 4, endLine: 6, anchor: 'L4', excerpt: null });

beforeEach(() => {
  renderers.failPdf = false;
  renderers.pdfRendered = [];
  renderers.docxParagraphs = null;
  renderers.pdfOpened = 0;
  renderers.docxRendered = 0;
  renderers.pdfScales = [];
  api.getById.mockReset();
  api.getOriginal.mockReset();
  api.attachOriginal.mockReset();
  auth.roles = [];
  api.getOriginal.mockResolvedValue(new ArrayBuffer(16));
  window.history.replaceState(null, '', '/projects/p1/validation?cq=CQ-004');
  window.scrollTo = vi.fn() as unknown as typeof window.scrollTo;
  HTMLIFrameElement.prototype.scrollIntoView = () => {};
});

afterEach(cleanup);

const openLink = (name: RegExp) => {
  const link = screen.getByRole('button', { name });
  link.focus();
  fireEvent.click(link);
  return link;
};

describe('opening a source link', () => {
  test('opens the original PDF in a modal on the same route, at the cited page, with the heading highlighted', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    card(reason, [twelve, threeTwo]);
    const before = window.location.href;

    openLink(/^Section 12\.7: open/);

    const dialog = await screen.findByRole('dialog', { name: 'Triage SRS.pdf' });
    expect(window.location.href).toBe(before); // no navigation, no new tab
    await screen.findByText(/Page 3: the cited section is highlighted/);
    const mark = dialog.querySelector('.src-pdf-page[data-page="3"] mark.src-hit')!;
    expect(mark.textContent).toBe('12.7 Decline Categories');
    expect(mark.getAttribute('aria-current')).toBe('location');
    expect(renderers.pdfRendered[0]).toBe(3); // the cited page is drawn first
    expect(api.getById).toHaveBeenCalledWith(SOURCE_ID, 'test-token');
    expect(api.getOriginal).toHaveBeenCalledWith(SOURCE_ID, 'test-token', expect.any(AbortSignal));
    expect(dialog.textContent).toContain('The original PDF uploaded as Triage SRS.pdf.');
  });

  test('a quote is highlighted as the passage, on its page', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    const quote = ref({ label: 'listed', start: 0, line: 10, endLine: 11, excerpt: 'listed in the decline popup', excerptIsQuote: true });
    card('listed here', [quote]);

    openLink(/^listed: open/);

    await screen.findByText(/Page 3: the cited passage is highlighted/);
    const marks = Array.from(document.querySelectorAll('.src-pdf-page[data-page="3"] mark.src-hit')).map((m) => m.textContent);
    expect(marks).toEqual(['listed in the decline popup']); // only the passage is yellow
    // Its heading, on the same page, is underlined for context - not highlighted.
    expect(document.querySelector('.src-pdf-page[data-page="3"] mark.src-heading-mark')!.textContent).toBe('12.7 Decline Categories');
  });

  test('opens the original DOCX at the cited paragraphs, telling a repeated heading apart', async () => {
    api.getById.mockResolvedValue(source('docx'));
    // The second "Scope" (under 2 Security), not the first.
    const scope = ref({ label: 'Scope', start: 0, sectionNumber: null, heading: 'Scope', line: 7, endLine: 9, excerpt: null });
    card('Scope is unclear.', [scope]);

    openLink(/^Scope: open/);

    await screen.findByText(/the cited section is highlighted/);
    const frame = document.querySelector('iframe.src-docx') as HTMLIFrameElement;
    expect(frame.getAttribute('sandbox')).toBe('allow-same-origin'); // no scripts, popups or navigation
    const cited = Array.from(frame.contentDocument!.querySelectorAll('.src-docx-cited')).map((p) => p.textContent);
    expect(cited).toEqual(['Scope', 'Passwords are stored hashed.']);
    const paragraphs = Array.from(frame.contentDocument!.querySelectorAll('p'));
    expect(paragraphs.indexOf(frame.contentDocument!.querySelector('.src-docx-heading')!)).toBe(8); // after "2 Security"
  });

  test('a repeated PDF heading is marked on its own line, past the same words earlier on the page', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    const scope = ref({ label: 'Scope', start: 0, sectionNumber: null, heading: 'Scope', line: 7, endLine: 9, excerpt: null });
    card('Scope is unclear.', [scope]);

    openLink(/^Scope: open/);

    await screen.findByText(/Page 2: the cited section is highlighted/);
    const mark = document.querySelector('.src-pdf-page[data-page="2"] mark.src-hit')!;
    expect(mark.parentElement!.textContent).toBe('Scope'); // not the "scope" inside line 5
  });

  test('each of several links opens its own place', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    card(reason, [twelve, threeTwo]);

    openLink(/^§3\.2: open/);
    await screen.findByText(/Page 2: the cited section is highlighted/);
    expect(document.querySelector('mark.src-hit')!.textContent).toBe('3.2 User Roles');
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));

    openLink(/^Section 12\.7: open/);
    await screen.findByText(/Page 3: the cited section is highlighted/);
    expect(document.querySelector('mark.src-hit')!.textContent).toBe('12.7 Decline Categories');
  });

  test('a reference the source does not hold highlights nothing, in the original or the text', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    card('See Section 9.9.', [ref({ label: 'Section 9.9', start: 4, sectionNumber: '9.9', heading: '9.9 Auditors', line: 2, endLine: 3 })]);

    openLink(/^Section 9\.9: open/);

    await screen.findByText(/could not be located in this source, so nothing is highlighted/);
    await waitFor(() => expect(renderers.pdfRendered).toEqual([1]));
    expect(document.querySelector('mark')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Show the extracted text' }));
    expect(document.querySelector('.src-line-cited, mark')).toBeNull();
  });

  test('closing restores focus to the link and leaves the page where it was', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    card(reason, [twelve]);
    window.scrollY = 640;

    const link = openLink(/^Section 12\.7: open/);
    await screen.findByRole('dialog');
    await screen.findByText(/highlighted/);
    fireEvent.keyDown(document, { key: 'Escape' });

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(link);
    expect(window.scrollY).toBe(640);
    expect(window.scrollTo).not.toHaveBeenCalled();
    expect(link.isConnected).toBe(true); // the page underneath was kept, not re-rendered
  });
});

describe('viewer states', () => {
  test('shows loading, then the access-denied message for a 403', async () => {
    let reject!: (e: unknown) => void;
    api.getById.mockReturnValue(new Promise((_, r) => (reject = r)));
    card(reason, [twelve]);

    openLink(/^Section 12\.7: open/);
    expect(await screen.findByText('Loading the requirement source…')).toBeTruthy();
    await act(async () => reject(new ApiError(403, 'forbidden')));

    expect(await screen.findByText('You can’t open this source')).toBeTruthy();
    expect(api.getOriginal).not.toHaveBeenCalled();
  });

  test('a missing original says so and offers the extracted text, without putting it in place of the original', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    api.getOriginal.mockRejectedValue(new ApiError(404, '{"type":"urn:aireap:original-unavailable"}'));
    card(reason, [twelve]);

    openLink(/^Section 12\.7: open/);

    expect((await screen.findByRole('alert')).textContent).toContain('The original PDF (Triage SRS.pdf) is no longer stored');
    expect(document.querySelector('.src-viewer-doc')).toBeNull(); // no text in its place
    expect(screen.queryByRole('button', { name: 'Try again' })).toBeNull(); // a stored file that is gone does not come back
    expect(screen.queryByRole('button', { name: /Attach the original/ })).toBeNull(); // not a writer

    fireEvent.click(screen.getByRole('button', { name: 'Show the extracted text instead' }));
    expect(document.querySelector('.src-line-heading')!.textContent).toContain('12.7 Decline Categories');
    expect(screen.getByText(/The original could not be shown, so you are viewing the text extracted from it/)).toBeTruthy();
  });

  test('a writer can attach the original of a legacy upload, and the viewer then shows it', async () => {
    auth.roles = ['BusinessAnalyst'];
    api.getById.mockResolvedValueOnce(source(null)).mockResolvedValue(source('pdf'));
    api.attachOriginal.mockResolvedValue(source('pdf'));
    card(reason, [twelve]);
    openLink(/^Section 12\.7: open/);

    fireEvent.click(await screen.findByRole('button', { name: 'Attach the original PDF' }));
    const file = new File(['%PDF-1.7'], 'Triage SRS.pdf', { type: 'application/pdf' });
    fireEvent.change(document.querySelector('.src-viewer-attach input[type=file]')!, { target: { files: [file] } });

    await waitFor(() => expect(api.attachOriginal).toHaveBeenCalledWith(SOURCE_ID, file, 'test-token'));
    expect(await screen.findByText(/The original PDF uploaded as Triage SRS\.pdf/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Attach the original/ })).toBeNull();
  });

  test('a different document is refused and the text stays', async () => {
    auth.roles = ['Administrator'];
    api.getById.mockResolvedValue(source(null));
    api.attachOriginal.mockRejectedValue(new ApiError(422, '{}'));
    card(reason, [twelve]);
    openLink(/^Section 12\.7: open/);

    fireEvent.click(await screen.findByRole('button', { name: 'Attach the original PDF' }));
    const file = new File(['%PDF-1.7'], 'Other.pdf', { type: 'application/pdf' });
    fireEvent.change(document.querySelector('.src-viewer-attach input[type=file]')!, { target: { files: [file] } });

    expect((await screen.findByRole('alert')).textContent).toContain('Other.pdf is not the document that was uploaded');
    expect(api.getById).toHaveBeenCalledTimes(1);
  });

  test('a PDF that cannot be rendered says so, with Try again, and shows the text only when asked', async () => {
    renderers.failPdf = true;
    api.getById.mockResolvedValue(source('pdf'));
    card(reason, [twelve]);

    openLink(/^Section 12\.7: open/);

    expect((await screen.findByRole('alert')).textContent).toContain('The original PDF (Triage SRS.pdf) could not be displayed.');
    expect(document.querySelector('.src-viewer-doc, .src-line-cited')).toBeNull();

    // Try again loads the source and its original afresh - and this time it draws.
    renderers.failPdf = false;
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await screen.findByText(/Page 3: the cited section is highlighted/);
    expect(api.getOriginal).toHaveBeenCalledTimes(2);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  test('an original that could not be loaded offers the text as a choice, and the original back from it', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    api.getOriginal.mockRejectedValue(new TypeError('Failed to fetch'));
    card(reason, [twelve]);

    openLink(/^Section 12\.7: open/);

    expect((await screen.findByRole('alert')).textContent).toContain('could not be loaded. Check your connection and try again.');
    fireEvent.click(screen.getByRole('button', { name: 'Show the extracted text instead' }));
    expect(document.querySelector('.src-line-cited')).not.toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Show the original PDF' }));
    expect(screen.getByRole('alert').textContent).toContain('could not be loaded');
    expect(document.querySelector('.src-viewer-doc')).toBeNull();
  });

  test('a legacy source without a kept original shows its text and never asks for an original', async () => {
    api.getById.mockResolvedValue(source(null));
    card(reason, [twelve]);

    openLink(/^Section 12\.7: open/);

    expect(await screen.findByText(/Only this text is kept/)).toBeTruthy();
    expect(api.getOriginal).not.toHaveBeenCalled();
    expect(document.querySelector('.src-line-heading')!.textContent).toContain('12.7 Decline Categories');
  });

  test('markup in the source text is shown as text, never run', async () => {
    const hostile = source(null);
    hostile.rawText = ['12.7 Decline Categories', '<img src=x onerror="window.__pwned=1"><script>window.__pwned=2</script>'].join('\n');
    api.getById.mockResolvedValue(hostile);
    card(reason, [ref({ start: reason.indexOf('Section 12.7'), line: 0, endLine: 2 })]);

    openLink(/^Section 12\.7: open/);

    expect(await screen.findByText(/<img src=x onerror=/)).toBeTruthy();
    expect(document.querySelector('.src-viewer-doc img, .src-viewer-doc script')).toBeNull();
    expect((window as unknown as { __pwned?: number }).__pwned).toBeUndefined();
  });
});

// ---- the passage behind a question (ClarificationQuestionCard evidence) ----------------------------

function cardWith(data: Record<string, unknown>) {
  const artifact = {
    id: 'a2',
    code: 'CQ-007',
    requirementSourceId: SOURCE_ID,
    data: { question: 'Which decline categories apply?', clarificationStatus: 'Open', sourceReferences: [], ...data },
  } as unknown as ArtifactSummary;
  return render(
    <ul>
      <ClarificationQuestionCard artifact={artifact} canAnswer={false} onAnswer={async () => {}} />
    </ul>,
  );
}

// A quote reference to one whole line of LINES, as the server places evidence.
const quoteAt = (line: number, heading: string, sectionNumber: string | null, over: Partial<SourceReference> = {}) =>
  ref({ label: LINES[line], start: 0, line, endLine: line + 1, anchor: `L${line}`, excerpt: LINES[line], excerptIsQuote: true, heading, sectionNumber, ...over });

const decline = quoteAt(10, '12.7 Decline Categories', '12.7');
const invoices = quoteAt(3, '1 Billing', '1');
const passwords = quoteAt(8, '2 Security', '2');

describe('a question linked to the passage that raised it', () => {
  test('the card shows the question and one source link named by its section - no "Why it matters", reason or impact', () => {
    cardWith({
      kind: 'ambiguous',
      reason: 'The popup is not described.',
      impact: 'Decides what the popup offers.',
      requirementRefs: ['12.7 Decline Categories'],
      sourceEvidence: [decline],
      sourceLinkStatus: 'passage',
    });

    const card = document.querySelector('.cq-card')!;
    expect(card.querySelector('.cq-question')!.textContent).toBe('Which decline categories apply?');
    expect(card.textContent).not.toMatch(/Why it matters/i);
    expect(card.textContent).not.toContain('The popup is not described.');
    expect(card.textContent).not.toContain('Decides what the popup offers.');
    expect(card.textContent).not.toMatch(/could be verified|Browse the source|line \d/);
    const links = screen.getAllByRole('button', { name: /highlighted passage/ });
    expect(links).toHaveLength(1);
    expect(links[0].textContent).toBe('12.7 Decline Categories');
    expect(links[0].getAttribute('aria-label')).toBe('Open “12.7 Decline Categories” in the requirement source at the highlighted passage');
  });

  test('an ambiguous statement: the source link opens the original PDF and highlights exactly it', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    cardWith({ kind: 'ambiguous', reason: 'The popup is not described.', impact: 'Decides what the popup offers.', sourceEvidence: [decline], sourceLinkStatus: 'passage' });

    openLink(/^Open “12\.7 Decline Categories” in the requirement source/);

    const dialog = await screen.findByRole('dialog', { name: 'Triage SRS.pdf' });
    await screen.findByText(/Page 3: the cited passage is highlighted/);
    // The statement itself is the place navigated to; its heading is marked too, for context.
    expect(dialog.querySelector('.src-pdf-page[data-page="3"] mark.src-hit[aria-current="location"]')!.textContent).toBe(LINES[10]);
    expect(dialog.querySelector('.src-viewer-evidence')!.textContent).toContain('Ambiguous statement');
  });

  test('in the original DOCX only the quoted paragraph is cited, with its section heading marked apart', async () => {
    api.getById.mockResolvedValue(source('docx'));
    cardWith({ kind: 'ambiguous', sourceEvidence: [decline] });

    openLink(/^Open “12\.7 Decline Categories”/);

    await screen.findByText(/the cited passage is highlighted/);
    const doc = (document.querySelector('iframe.src-docx') as HTMLIFrameElement).contentDocument!;
    expect(Array.from(doc.querySelectorAll('.src-docx-cited')).map((p) => p.textContent)).toEqual([LINES[10]]);
    const heading = doc.querySelector('.src-docx-heading')!;
    expect(heading.textContent).toBe('12.7 Decline Categories');
    expect(heading.classList.contains('src-docx-cited')).toBe(false);
    expect(doc.querySelector('mark.src-hit')!.textContent).toBe(LINES[10]);
  });

  test('a conflict: each statement has its own link, and both can be stepped through and highlighted in one viewer', async () => {
    api.getById.mockResolvedValue(source(null));
    cardWith({ kind: 'contradiction', sourceEvidence: [invoices, passwords] });

    const links = screen.getAllByRole('button', { name: /highlighted passage/ });
    expect(links.map((l) => l.querySelector('.cq-source-label')!.textContent)).toEqual(['1 Billing', '2 Security']);
    expect(links.map((l) => l.querySelector('.cq-source-statement')!.textContent)).toEqual(['Statement 1', 'Statement 2']);
    openLink(/^Statement 1: Open “1 Billing”/);

    await screen.findByRole('group', { name: 'Passages of this question' });
    await waitFor(() => expect(document.querySelector('.src-line-cited')?.getAttribute('data-line')).toBe('3'));
    expect(document.querySelector('.src-line-related')!.getAttribute('data-line')).toBe('8');
    expect(document.querySelector('.src-viewer-evidence')!.textContent).toContain('Conflicting statement');

    fireEvent.click(screen.getByRole('button', { name: 'Passage 2 of 2 (2 Security)' }));

    expect(document.querySelector('.src-line-cited')!.getAttribute('data-line')).toBe('8');
    expect(document.querySelector('.src-line-related')!.getAttribute('data-line')).toBe('3');
    expect(document.querySelector('.src-line-cited mark')!.textContent).toBe('Passwords are stored hashed.');
  });

  test('missing information: the link opens the requirement that lacks the detail, as its context', async () => {
    api.getById.mockResolvedValue(source(null));
    cardWith({ kind: 'missing', reason: 'Who may decline is not stated.', impact: 'i', sourceEvidence: [decline] });

    openLink(/^Open “12\.7 Decline Categories”/);

    const label = await screen.findByText('Context for the missing information');
    expect(label.closest('.src-viewer-evidence')!.textContent).toContain(LINES[10]);
    expect(document.querySelector('.src-line-cited')!.getAttribute('data-line')).toBe('10');
  });

  test.each([
    ['missing', 'Context for the missing information'],
    ['ambiguous', 'Ambiguous statement'],
    ['contradiction', 'Conflicting statement'],
    ['undefined-rule', null],
  ])('the viewer shows the complete question (%s) above the statement and the document', async (kind, purposeLabel) => {
    api.getById.mockResolvedValue(source(null));
    const question = `For the ${kind} case: which decline categories apply, and who may choose each one?`;
    cardWith({ kind, question, sourceEvidence: [decline] });

    openLink(/^Open “12\.7 Decline Categories”/);

    const block = await screen.findByRole('region', { name: 'Clarification question' });
    expect(block.querySelector('p')!.textContent).toBe(question);
    const evidence = document.querySelector('.src-viewer-evidence')!;
    if (purposeLabel) expect(evidence.textContent).toContain(purposeLabel);
    // Order: question, then the statement, then the highlighted document.
    expect(block.compareDocumentPosition(evidence) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(evidence.compareDocumentPosition(document.querySelector('.src-viewer-doc')!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(document.querySelector('.src-line-cited')!.getAttribute('data-line')).toBe('10');
  });

  test('a conflict keeps the same question, below the passage tabs, while stepping between its statements', async () => {
    api.getById.mockResolvedValue(source(null));
    cardWith({ kind: 'contradiction', question: 'Are invoices or passwords the binding rule?', sourceEvidence: [invoices, passwords] });

    openLink(/^Statement 2: Open “2 Security”/);

    const tabs = await screen.findByRole('group', { name: 'Passages of this question' });
    const block = screen.getByRole('region', { name: 'Clarification question' });
    expect(tabs.compareDocumentPosition(block) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(block.textContent).toContain('Are invoices or passwords the binding rule?');

    fireEvent.click(screen.getByRole('button', { name: 'Passage 1 of 2 (1 Billing)' }));

    expect(document.querySelector('.src-line-cited')!.getAttribute('data-line')).toBe('3');
    expect(screen.getAllByRole('region', { name: 'Clarification question' })).toHaveLength(1);
    expect(screen.getByRole('region', { name: 'Clarification question' }).textContent).toContain('Are invoices or passwords the binding rule?');
  });

  test('each card opens the viewer with its own question, never another one', async () => {
    api.getById.mockResolvedValue(source(null));
    const make = (id: string, question: string, evidence: SourceReference) =>
      ({ id, code: id, requirementSourceId: SOURCE_ID, data: { question, clarificationStatus: 'Open', kind: 'ambiguous', sourceEvidence: [evidence] } }) as unknown as ArtifactSummary;
    render(
      <ul>
        <ClarificationQuestionCard artifact={make('CQ-1', 'First question?', decline)} canAnswer={false} onAnswer={async () => {}} />
        <ClarificationQuestionCard artifact={make('CQ-2', 'Second question?', invoices)} canAnswer={false} onAnswer={async () => {}} />
      </ul>,
    );

    openLink(/^Open “1 Billing”/);
    const block = await screen.findByRole('region', { name: 'Clarification question' });
    expect(block.textContent).toContain('Second question?');
    expect(block.textContent).not.toContain('First question?');
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());

    openLink(/^Open “12\.7 Decline Categories”/);
    const again = await screen.findByRole('region', { name: 'Clarification question' });
    expect(again.textContent).toContain('First question?');
    expect(again.textContent).not.toContain('Second question?');
  });

  test('the viewer opened without a question (a plain section link) shows no question block', async () => {
    api.getById.mockResolvedValue(source(null));
    card(reason, [twelve]);

    openLink(/^Section 12\.7: open/);

    await screen.findByRole('dialog');
    await waitFor(() => expect(document.querySelector('.src-viewer-doc')).toBeTruthy());
    expect(screen.queryByRole('region', { name: 'Clarification question' })).toBeNull();
  });

  test('a passage outside any section is linked without inventing a heading', () => {
    cardWith({ kind: 'ambiguous', sourceEvidence: [quoteAt(0, '', null, { heading: null })] });

    const link = screen.getByRole('button', { name: /highlighted passage/ });
    expect(link.textContent).toBe('Source passage');
    expect(link.getAttribute('aria-label')).toBe('Open the requirement source at the highlighted passage');
  });

  test('evidence the server could not place, or a caller who gets no passages (S2), has no link and no warning', () => {
    cardWith({ kind: 'ambiguous', sourceEvidence: [quoteAt(3, '1 Billing', '1', { line: -1, endLine: -1 })] });
    expect(screen.queryByRole('button', { name: /highlighted passage/ })).toBeNull();
    cleanup();

    cardWith({ kind: 'ambiguous' });
    expect(screen.queryByRole('button', { name: /highlighted passage/ })).toBeNull();
    expect(document.querySelector('.cq-card')!.textContent).not.toMatch(/verified|Browse/);
  });

  test('answering still works from the card', async () => {
    const onAnswer = vi.fn(async () => {});
    const artifact = {
      id: 'a3',
      code: 'CQ-009',
      requirementSourceId: SOURCE_ID,
      data: { question: 'Who may decline?', clarificationStatus: 'Open', kind: 'missing', sourceEvidence: [decline] },
    } as unknown as ArtifactSummary;
    render(
      <ul>
        <ClarificationQuestionCard artifact={artifact} canAnswer onAnswer={onAnswer} />
      </ul>,
    );

    fireEvent.change(screen.getByRole('textbox', { name: 'Answer for CQ-009' }), { target: { value: 'Senior nurses' } });
    fireEvent.click(screen.getByRole('button', { name: 'Submit answer' }));
    await waitFor(() => expect(onAnswer).toHaveBeenCalledWith('a3', 'Senior nurses', false));
    fireEvent.click(screen.getByRole('button', { name: 'Not applicable' }));
    await waitFor(() => expect(onAnswer).toHaveBeenCalledWith('a3', 'Senior nurses', true));
  });

  test('opening a source link only reads the source - no analysis or AI endpoint is called', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    cardWith({ kind: 'ambiguous', sourceEvidence: [decline] });

    openLink(/^Open “12\.7 Decline Categories”/);
    await screen.findByRole('dialog');
    await waitFor(() => expect(api.getOriginal).toHaveBeenCalledTimes(1));
    expect(api.getById).toHaveBeenCalledTimes(1);
    // The mocked API holds only the read endpoints: an analysis or regeneration call would have failed.
    expect(Object.keys(api).sort()).toEqual(['attachOriginal', 'getById', 'getOriginal']);
    expect(api.attachOriginal).not.toHaveBeenCalled();
  });
});

// ---- several passages side by side (SourceDocumentViewer panels) ------------------------------------

const roles = quoteAt(5, '3.2 User Roles', '3.2');
const title = quoteAt(0, '', null, { heading: null });
const FIVE = [invoices, passwords, decline, roles, title];
const OTHER_ID = '0c3d9e21-4b8f-4a51-8d7e-5f1a2b3c4d5e';
const OTHER_LINES = ['Careway Portal SRS', '4 Notifications', 'Reminders are sent by text message only.'];
const otherSource = (): RequirementSource => ({ ...source(null), id: OTHER_ID, rawText: OTHER_LINES.join('\n'), originalFileName: 'Careway SRS.docx' }) as RequirementSource;
const reminders = ref({ label: OTHER_LINES[2], sourceId: OTHER_ID, line: 2, endLine: 3, anchor: 'L2', excerpt: OTHER_LINES[2], excerptIsQuote: true, heading: '4 Notifications', sectionNumber: '4' });

const panelList = () => screen.getByRole('list', { name: 'Passages side by side' });
const panels = () => within(panelList()).getAllByRole('listitem');
const citedLine = (panel: HTMLElement) => panel.querySelector('.src-line-cited')?.getAttribute('data-line');
const addPassage = () => fireEvent.click(screen.getByRole('button', { name: 'Add a passage panel' }));

describe('comparing several passages side by side', () => {
  test('a question with one passage opens the single view, with nothing to add', async () => {
    api.getById.mockResolvedValue(source(null));
    cardWith({ kind: 'ambiguous', question: 'Which categories?', sourceEvidence: [decline] });

    openLink(/^Open “12\.7 Decline Categories”/);

    await waitFor(() => expect(citedLine(document.body)).toBe('10'));
    expect(screen.queryByRole('button', { name: 'Add a passage panel' })).toBeNull();
    expect(screen.queryByRole('list', { name: 'Passages side by side' })).toBeNull();
  });

  test.each([2, 3, 4, 5])('%i passages of one document can all be shown at once, each highlighted in its own panel, fetched once', async (count) => {
    api.getById.mockResolvedValue(source(null));
    const evidence = FIVE.slice(0, count);
    cardWith({ kind: 'ambiguous', question: 'Which rule applies?', sourceEvidence: evidence });

    openLink(/^Open “1 Billing”/);
    await screen.findByRole('group', { name: 'Passages of this question' });
    for (let n = 1; n < count; n++) addPassage();

    expect(panels()).toHaveLength(count);
    await waitFor(() => expect(panels().map(citedLine)).toEqual(evidence.map((e) => String(e.line))));
    panels().forEach((panel, i) => {
      expect(panel.getAttribute('aria-label')).toBe(`Panel ${i + 1}: Passage ${i + 1} of ${count}`);
      expect(panel.querySelector('.src-line-cited mark')!.textContent).toBe(LINES[evidence[i].line]);
      expect(panel.querySelector('.src-viewer-evidence')!.textContent).toContain(LINES[evidence[i].line]);
      expect(panel.querySelectorAll('.src-viewer-doc')).toHaveLength(1);
    });
    // Every passage is shown: nothing more to add, and no passage twice.
    expect((screen.getByRole('button', { name: 'Add a passage panel' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByRole('group', { name: 'Passages of this question' })).toBeNull();
    expect(api.getById).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('dialog', { name: 'Triage SRS.pdf' })).toBeTruthy();
    // The dialog title names the one document, so the panels name only their section.
    panels().forEach((panel, i) => {
      expect(panel.querySelector('.src-panel-file')).toBeNull();
      expect(panel.querySelector('.src-panel-ref')!.textContent).toBe(sourceLinkLabel(evidence[i]));
      expect(panel.querySelector('.src-panel-title')!.getAttribute('title')).toBe(`Triage SRS.pdf · ${sourceLinkLabel(evidence[i])}`);
    });
  });

  test('the question is shown once, above all the panels, and stays while panels are added, replaced and removed', async () => {
    api.getById.mockResolvedValue(source(null));
    const question = 'Are invoices, passwords or decline categories the binding rule?';
    cardWith({ kind: 'contradiction', question, sourceEvidence: [invoices, passwords, decline] });

    openLink(/^Statement 1: Open “1 Billing”/);
    await screen.findByRole('group', { name: 'Passages of this question' });
    addPassage();
    addPassage();

    const check = () => {
      const blocks = screen.getAllByRole('region', { name: 'Clarification question' });
      expect(blocks).toHaveLength(1);
      expect(blocks[0].querySelector('p')!.textContent).toBe(question);
      expect(blocks[0].compareDocumentPosition(panelList()) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      expect(panelList().contains(blocks[0])).toBe(false);
    };
    await waitFor(() => expect(panels().map(citedLine)).toEqual(['3', '8', '10']));
    check();
    fireEvent.click(screen.getByRole('button', { name: 'Remove panel 3' }));
    check();
    fireEvent.change(screen.getByRole('combobox', { name: 'Passage shown in panel 2' }), { target: { value: '2' } });
    check();
    expect(panels().map(citedLine)).toEqual(['3', '10']);
    expect(panels()[0].querySelector('.src-viewer-evidence')!.textContent).toContain('Conflicting statement');
  });

  test('replacing the passage of one panel leaves the others - and their scroll position - as they were', async () => {
    api.getById.mockResolvedValue(source(null));
    cardWith({ kind: 'ambiguous', sourceEvidence: [invoices, passwords, decline, roles] });

    openLink(/^Open “1 Billing”/);
    await screen.findByRole('group', { name: 'Passages of this question' });
    addPassage();
    addPassage();
    await waitFor(() => expect(panels().map(citedLine)).toEqual(['3', '8', '10']));

    const [firstDoc, secondDoc, thirdDoc] = panels().map((p) => p.querySelector<HTMLElement>('.src-viewer-doc')!);
    firstDoc.scrollTop = 40;
    thirdDoc.scrollTop = 90;
    secondDoc.scrollTop = 7; // scrolling one pane moves only that pane
    expect([firstDoc.scrollTop, thirdDoc.scrollTop]).toEqual([40, 90]);

    // Passages shown in another panel are offered but cannot be picked again.
    const select = screen.getByRole('combobox', { name: 'Passage shown in panel 2' }) as HTMLSelectElement;
    expect(Array.from(select.options).map((o) => o.disabled)).toEqual([true, false, true, false]);
    expect(select.options[0].textContent).toContain('already shown');
    fireEvent.change(select, { target: { value: '3' } });

    expect(panels().map(citedLine)).toEqual(['3', '5', '10']);
    expect(panels()[1].getAttribute('aria-label')).toBe('Panel 2: Passage 4 of 4');
    expect(panels()[0].querySelector('.src-viewer-doc')).toBe(firstDoc);
    expect(panels()[2].querySelector('.src-viewer-doc')).toBe(thirdDoc);
    expect([firstDoc.scrollTop, thirdDoc.scrollTop]).toEqual([40, 90]);
    expect(api.getById).toHaveBeenCalledTimes(1);
  });

  test('panels can be moved and removed; down to one, the passage tabs come back on the passage left', async () => {
    api.getById.mockResolvedValue(source(null));
    cardWith({ kind: 'ambiguous', sourceEvidence: [invoices, passwords, decline] });

    openLink(/^Open “1 Billing”/);
    await screen.findByRole('group', { name: 'Passages of this question' });
    addPassage();
    addPassage();
    await waitFor(() => expect(panels().map(citedLine)).toEqual(['3', '8', '10']));

    expect((screen.getByRole('button', { name: 'Move panel 1 left' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Move panel 3 right' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Move panel 3 left' }));
    expect(panels().map(citedLine)).toEqual(['3', '10', '8']);
    fireEvent.click(screen.getByRole('button', { name: 'Move panel 1 right' }));
    expect(panels().map(citedLine)).toEqual(['10', '3', '8']);

    fireEvent.click(screen.getByRole('button', { name: 'Remove panel 2' }));
    expect(panels().map(citedLine)).toEqual(['10', '8']);
    expect((screen.getByRole('button', { name: 'Add a passage panel' }) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Remove panel 2' }));

    expect(screen.queryByRole('list', { name: 'Passages side by side' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Passage 3 of 3 (12.7 Decline Categories)' }).getAttribute('aria-pressed')).toBe('true');
    expect(citedLine(document.body)).toBe('10');
    // The tabs still step between passages as before.
    fireEvent.click(screen.getByRole('button', { name: 'Passage 1 of 3 (1 Billing)' }));
    expect(citedLine(document.body)).toBe('3');
  });

  test('passages from different documents: each panel loads and names its own document, each document once', async () => {
    api.getById.mockImplementation(async (id: string) => (id === OTHER_ID ? otherSource() : source('pdf')));
    cardWith({ kind: 'contradiction', question: 'Text message or invoice?', sourceEvidence: [invoices, reminders, passwords] });

    openLink(/^Statement 1: Open “1 Billing”/);
    await screen.findByRole('group', { name: 'Passages of this question' });
    addPassage();
    addPassage();

    await waitFor(() => expect(screen.getByRole('dialog', { name: 'Compare source passages' })).toBeTruthy());
    const [first, second, third] = panels();
    await waitFor(() => expect(first.querySelector('.src-pdf-page[data-page="1"] mark.src-hit[aria-current="location"]')?.textContent).toBe(LINES[3]));
    await waitFor(() => expect(third.querySelector('.src-pdf-page[data-page="2"] mark.src-hit[aria-current="location"]')?.textContent).toBe(LINES[8]));
    expect(first.querySelector('.src-panel-file')!.textContent).toBe('Triage SRS.pdf');
    expect(second.querySelector('.src-panel-file')!.textContent).toBe('Careway SRS.docx');
    expect(second.querySelector('.src-panel-ref')!.textContent).toBe('4 Notifications');
    expect(citedLine(second)).toBe('2');
    expect(second.querySelector('.src-line-cited mark')!.textContent).toBe(OTHER_LINES[2]);

    expect(api.getById.mock.calls.map((c) => c[0]).sort()).toEqual([OTHER_ID, SOURCE_ID].sort());
    expect(api.getOriginal).toHaveBeenCalledTimes(1);
    expect(api.getOriginal).toHaveBeenCalledWith(SOURCE_ID, 'test-token', expect.any(AbortSignal));
  });

  test('a panel switches to the extracted text on its own, without changing the other panels', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    cardWith({ kind: 'ambiguous', sourceEvidence: [invoices, passwords] });

    openLink(/^Open “1 Billing”/);
    await screen.findByRole('group', { name: 'Passages of this question' });
    addPassage();
    await waitFor(() => expect(panels().every((p) => p.querySelector('mark.src-hit[aria-current="location"]'))).toBe(true));

    fireEvent.click(screen.getByRole('button', { name: 'Show the extracted text in panel 2' }));

    const [first, second] = panels();
    expect(first.querySelector('.src-original')).not.toBeNull();
    expect(first.querySelector('.src-viewer-doc')).toBeNull();
    expect(citedLine(second)).toBe('8');
    expect(screen.getByRole('button', { name: 'Show the original PDF in panel 2' }).getAttribute('aria-pressed')).toBe('true');
    expect(api.getOriginal).toHaveBeenCalledTimes(1);
  });
  test('the passage picker shows short, numbered labels with the full section name as a tooltip', async () => {
    api.getById.mockResolvedValue(source(null));
    const long = quoteAt(5, '3.2\tUser Roles and the permissions each of them is granted across every module', '3.2');
    cardWith({ kind: 'ambiguous', sourceEvidence: [invoices, long, decline] });

    openLink(/^Open “1 Billing”/);
    await screen.findByRole('group', { name: 'Passages of this question' });
    addPassage();

    const select = screen.getByRole('combobox', { name: 'Passage shown in panel 2' }) as HTMLSelectElement;
    const options = Array.from(select.options);
    expect(options[0].textContent).toBe('1 of 3 · 1 Billing – already shown');
    expect(options[1].textContent).toMatch(/^2 of 3 · 3\.2 User Roles and the permissions each…$/);
    expect(options[1].textContent!.length).toBeLessThan(50);
    expect(options[1].title).toBe('Passage 2 of 3: 3.2 User Roles and the permissions each of them is granted across every module');
    expect(options[2].textContent).toBe('3 of 3 · 12.7 Decline Categories');
    expect(select.title).toBe(options[1].title);
    // The short label changes nothing about which passage is shown.
    expect(options.map((o) => o.value)).toEqual(['0', '1', '2']);
    await waitFor(() => expect(panels().map(citedLine)).toEqual(['3', '5']));
  });

  test('a panel names its file once, in one compact line with the section and the text toggle', async () => {
    api.getById.mockImplementation(async (id: string) => (id === OTHER_ID ? otherSource() : source('pdf')));
    cardWith({ kind: 'contradiction', sourceEvidence: [invoices, reminders] });

    openLink(/^Statement 1: Open “1 Billing”/);
    await screen.findByRole('group', { name: 'Passages of this question' });
    addPassage();

    const [first, second] = panels();
    await waitFor(() => expect(first.querySelector('mark.src-hit[aria-current="location"]')).not.toBeNull());
    await waitFor(() => expect(citedLine(second)).toBe('2'));
    // The original PDF: no note repeating the file name; the header line names it, with a tooltip.
    expect(first.textContent!.split('Triage SRS.pdf').length - 1).toBe(1);
    expect(first.querySelector('.src-viewer-note')).toBeNull();
    expect(first.querySelector('.src-panel-title')!.getAttribute('title')).toBe('Triage SRS.pdf · 1 Billing');
    expect(within(first).getByRole('button', { name: 'Show the extracted text in panel 1' })).toBeTruthy();
    // Extracted text only: the note says so, without naming the file again.
    expect(second.textContent!.split('Careway SRS.docx').length - 1).toBe(1);
    expect(second.querySelector('.src-viewer-note')!.textContent).toBe('Extracted paragraph text only: tables, headers, footers and images are not kept.');

    // Back in the original's extracted text, the reason is still given.
    fireEvent.click(screen.getByRole('button', { name: 'Show the extracted text in panel 1' }));
    expect(first.querySelector('.src-viewer-note')!.textContent).toContain('You are viewing the text extracted from it.');
    expect(first.querySelector('.src-viewer-note')!.textContent).not.toContain('Triage SRS.pdf');
  });
});

// ---- the yellow highlight of a CQ's exact passage ----------------------------------------------------
// The same rule in the PDF, the Word document and the extracted text: only the passage's own words (or,
// for a section reference, its heading) are yellow; the rest of the cited range has a bar, never a fill;
// the question's other passages in the same source are marked more lightly; and a passage that cannot
// be matched is not highlighted at all.

const frameDoc = () => (document.querySelector('iframe.src-docx') as HTMLIFrameElement).contentDocument!;
const hits = (root: ParentNode, selector = 'mark.src-hit:not(.src-hit-related)') => Array.from(root.querySelectorAll(selector)).map((m) => m.textContent);
const rect = (top: number) => ({ top, bottom: top + 20, left: 0, right: 100, width: 100, height: 20, x: 0, y: top, toJSON: () => ({}) }) as DOMRect;
const popup = ref({ label: 'listed', start: 0, line: 10, endLine: 11, excerpt: 'listed in the decline popup', excerptIsQuote: true });
const across = ref({ label: 'across', start: 0, line: 9, endLine: 11, excerpt: 'Decline Categories The eight decline', excerptIsQuote: true });

describe('the yellow highlight of a CQ passage', () => {
  test.each(['pdf', 'docx', null] as const)('%s: only the quoted words are marked, not the rest of their paragraph', async (format) => {
    api.getById.mockResolvedValue(source(format));
    card('listed here', [popup]);

    openLink(/^listed: open/);

    const root = format === 'docx' ? () => frameDoc() : () => document;
    await waitFor(() => expect(hits(root())).toEqual(['listed in the decline popup']));
    const mark = root().querySelector('mark.src-hit')!;
    // The paragraph keeps its other words, unmarked and in their places.
    expect(mark.parentElement!.textContent).toBe(LINES[10]);
    expect(mark.textContent!.length).toBeLessThan(LINES[10].length);
    if (format === 'docx') {
      const cited = frameDoc().querySelectorAll('.src-docx-cited');
      expect(Array.from(cited).map((p) => p.textContent)).toEqual([LINES[10]]); // located by a bar...
      expect(cited[0].tagName).toBe('P'); // ...not wrapped in a mark
    }
  });

  test.each(['pdf', 'docx', null] as const)('%s: a section reference is marked by its heading, the same in every format', async (format) => {
    api.getById.mockResolvedValue(source(format));
    card(reason, [twelve]);

    openLink(/^Section 12\.7: open/);

    const root = format === 'docx' ? () => frameDoc() : () => document;
    await waitFor(() => expect(hits(root())).toEqual(['12.7 Decline Categories']));
    // The section's body is located but not painted.
    expect(root().querySelector('[aria-current="location"]')!.textContent).toContain('12.7 Decline Categories');
    expect(hits(root(), 'mark')).toEqual(['12.7 Decline Categories']);
  });

  test('docx: a quote running over two paragraphs marks just its words in each, without filling either', async () => {
    api.getById.mockResolvedValue(source('docx'));
    card('across', [across]);

    openLink(/^across: open/);

    await screen.findByText(/the cited passage is highlighted/);
    expect(hits(frameDoc())).toEqual(['Decline Categories', 'The eight decline']);
    expect(frameDoc().querySelector('mark.src-hit')!.getAttribute('aria-current')).toBe('location');
  });

  test('text view: a quote over two lines is marked on both, and the pane opens at its first line', async () => {
    api.getById.mockResolvedValue(source(null));
    vi.spyOn(HTMLElement.prototype, 'offsetTop', 'get').mockImplementation(function (this: HTMLElement) {
      return this.getAttribute('aria-current') === 'location' ? 500 : 0;
    });
    card('across', [across]);

    openLink(/^across: open/);

    await waitFor(() => expect(hits(document)).toEqual(['Decline Categories', 'The eight decline']));
    expect(document.querySelector('[aria-current="location"]')!.getAttribute('data-line')).toBe('9');
    expect((document.querySelector('.src-viewer-doc') as HTMLElement).scrollTop).toBe(476);
  });

  test('pdf: clicking a CQ source link scrolls the pane to its highlighted passage', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (this: Element) {
      return rect(this.matches('mark.src-hit') ? 900 : 100);
    });
    cardWith({ kind: 'ambiguous', sourceEvidence: [decline] });

    openLink(/^Open “12\.7 Decline Categories”/);

    await screen.findByText(/Page 3: the cited passage is highlighted/);
    expect((document.querySelector('.src-pdf') as HTMLElement).scrollTop).toBe(900 - 100 - 24);
  });

  test.each(['docx', null] as const)('%s: a conflict keeps both statements highlighted, the shown one strongly, and swaps them when stepping', async (format) => {
    api.getById.mockResolvedValue(source(format));
    cardWith({ kind: 'contradiction', sourceEvidence: [invoices, passwords] });

    openLink(/^Statement 1: Open “1 Billing”/);

    const root = format === 'docx' ? () => frameDoc() : () => document;
    await waitFor(() => expect(hits(root())).toEqual([LINES[3]]));
    expect(hits(root(), 'mark.src-hit-related')).toEqual([LINES[8]]);

    fireEvent.click(screen.getByRole('button', { name: 'Passage 2 of 2 (2 Security)' }));

    await waitFor(() => expect(hits(root())).toEqual([LINES[8]]));
    expect(hits(root(), 'mark.src-hit-related')).toEqual([LINES[3]]);
  });

  test('pdf: another passage on the same page is marked lightly as the page is drawn', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    const heading = quoteAt(9, '12.7 Decline Categories', '12.7');
    cardWith({ kind: 'contradiction', sourceEvidence: [decline, heading] });

    openLink(/^Statement 1: Open/);

    await screen.findByText(/Page 3: the cited passage is highlighted/);
    expect(hits(document)).toEqual([LINES[10]]);
    expect(hits(document, '.src-pdf-page[data-page="3"] mark.src-hit-related')).toEqual([LINES[9]]);
  });

  test('a passage of another source is never searched for in this one', async () => {
    api.getById.mockImplementation(async (id: string) => (id === OTHER_ID ? otherSource() : source(null)));
    // Same heading and words as a passage of this source, but cited in the other document.
    const elsewhere = { ...decline, sourceId: OTHER_ID };
    render(<SourceDocumentViewer reference={invoices} passages={[invoices, elsewhere]} onClose={() => {}} />);

    await waitFor(() => expect(hits(document)).toEqual([LINES[3]]));
    expect(document.querySelector('mark.src-hit-related, .src-line-related')).toBeNull();
  });

  test('docx: quoted words not found in the cited paragraph highlight nothing, and say so', async () => {
    const lines = ['Intro', '4.1\tRetention rules', 'Records are kept for seven years.'];
    api.getById.mockResolvedValue({ ...source('docx'), rawText: lines.join('\n') });
    // Word draws the automatic number itself, so the paragraph's text is only "Retention rules".
    renderers.docxParagraphs = ['Cover', 'Intro', 'Retention rules', lines[2]];
    const numbered = ref({ label: 'Retention', start: 0, heading: null, sectionNumber: null, line: 1, endLine: 2, excerpt: '4.1 Retention rules', excerptIsQuote: true });
    card('Retention', [numbered]);

    openLink(/^Retention: open/);

    await screen.findByText(/Its exact words could not be matched there, so nothing is highlighted/);
    expect(frameDoc().querySelector('mark')).toBeNull();
    const at = frameDoc().querySelector('[aria-current="location"]')!;
    expect(at.textContent).toBe('Retention rules'); // opened at the right paragraph...
    expect(at.classList.contains('src-docx-cited')).toBe(true); // ...shown by its bar alone
  });

  test.each(['pdf', 'docx'] as const)('%s: an unmatched reference leaves the document unmarked', async (format) => {
    api.getById.mockResolvedValue(source(format));
    card('See Section 9.9.', [ref({ label: 'Section 9.9', start: 4, sectionNumber: '9.9', heading: '9.9 Auditors', line: 2, endLine: 3 })]);

    openLink(/^Section 9\.9: open/);

    await screen.findByText(/could not be located in this source, so nothing is highlighted/);
    if (format === 'docx') await waitFor(() => expect(frameDoc().querySelectorAll('p').length).toBeGreaterThan(0));
    else await waitFor(() => expect(renderers.pdfRendered).toEqual([1]));
    const root = format === 'docx' ? frameDoc() : document;
    expect(root.querySelector('mark, .src-docx-cited, [aria-current="location"]')).toBeNull();
  });
});

describe('the original document, opened at the passage', () => {
  const pdfPane = () => document.querySelector('.src-pdf') as HTMLElement;
  const pageMarks = (n: number, selector = 'mark.src-hit:not(.src-hit-related)') => hits(document, `.src-pdf-page[data-page="${n}"] ${selector}`);

  test('pdf: stepping to a passage on another page moves the highlight and the scroll, without loading the PDF again', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (this: Element) {
      // Where the mark is in the document, less how far the pane is scrolled - as a browser reports it.
      if (this.matches('mark.src-hit:not(.src-hit-related)')) return rect((this.closest('[data-page="2"]') ? 1500 : 300) - pdfPane().scrollTop);
      return rect(100);
    });
    cardWith({ kind: 'contradiction', sourceEvidence: [invoices, passwords] });

    openLink(/^Statement 1: Open “1 Billing”/);
    await screen.findByText(/Page 1: the cited passage is highlighted/);
    expect(pageMarks(1)).toEqual([LINES[3]]);
    expect(pdfPane().scrollTop).toBe(300 - 100 - 24);
    expect(screen.getByText('Page 1 of 3')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Passage 2 of 2 (2 Security)' }));

    await screen.findByText(/Page 2: the cited passage is highlighted/);
    expect(pageMarks(2)).toEqual([LINES[8]]);
    expect(document.querySelector('.src-pdf-page[data-page="2"] mark[aria-current="location"]')!.textContent).toBe(LINES[8]);
    // The first statement is still marked - lightly now - and no longer the current place.
    expect(pageMarks(1)).toEqual([]);
    expect(pageMarks(1, 'mark.src-hit-related')).toEqual([LINES[3]]);
    expect(document.querySelectorAll('[aria-current="location"]')).toHaveLength(1);
    expect(pdfPane().scrollTop).toBe(1500 - 100 - 24);
    expect(screen.getByText('Page 2 of 3')).toBeTruthy();
    // The same document, parsed and fetched once.
    expect(renderers.pdfOpened).toBe(1);
    expect(api.getOriginal).toHaveBeenCalledTimes(1);
  });

  test('pdf: stepping back restores the first passage exactly, with the page text left as it was', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    cardWith({ kind: 'contradiction', sourceEvidence: [invoices, passwords] });
    openLink(/^Statement 1: Open “1 Billing”/);
    await screen.findByText(/Page 1: the cited passage is highlighted/);
    const page1 = document.querySelector('.src-pdf-page[data-page="1"] .textLayer')!;
    const before = page1.textContent;

    fireEvent.click(screen.getByRole('button', { name: 'Next passage' }));
    await screen.findByText(/Page 2: the cited passage is highlighted/);
    fireEvent.click(screen.getByRole('button', { name: 'Previous passage' }));
    await screen.findByText(/Page 1: the cited passage is highlighted/);

    expect(pageMarks(1)).toEqual([LINES[3]]);
    expect(pageMarks(2)).toEqual([]);
    expect(pageMarks(2, 'mark.src-hit-related')).toEqual([LINES[8]]);
    expect(page1.textContent).toBe(before); // marks come and go; the words stay
    expect(page1.querySelectorAll('mark mark')).toHaveLength(0);
  });

  test('previous and next step through the passages and stop at either end', async () => {
    api.getById.mockResolvedValue(source(null));
    cardWith({ kind: 'contradiction', sourceEvidence: [invoices, passwords] });
    openLink(/^Statement 1: Open “1 Billing”/);
    await waitFor(() => expect(hits(document)).toEqual([LINES[3]]));

    const previous = screen.getByRole('button', { name: 'Previous passage' }) as HTMLButtonElement;
    const next = screen.getByRole('button', { name: 'Next passage' }) as HTMLButtonElement;
    expect(previous.disabled).toBe(true);
    fireEvent.click(next);
    expect(hits(document)).toEqual([LINES[8]]);
    expect(screen.getByRole('button', { name: 'Passage 2 of 2 (2 Security)' }).getAttribute('aria-pressed')).toBe('true');
    expect(next.disabled).toBe(true);
    fireEvent.click(previous);
    expect(hits(document)).toEqual([LINES[3]]);
  });

  test('docx: stepping moves the highlight and the cited bar in the same rendered document', async () => {
    api.getById.mockResolvedValue(source('docx'));
    cardWith({ kind: 'contradiction', sourceEvidence: [invoices, passwords] });
    openLink(/^Statement 1: Open “1 Billing”/);
    await waitFor(() => expect(hits(frameDoc())).toEqual([LINES[3]]));
    const doc = frameDoc();

    fireEvent.click(screen.getByRole('button', { name: 'Next passage' }));

    await waitFor(() => expect(hits(frameDoc())).toEqual([LINES[8]]));
    expect(frameDoc()).toBe(doc); // not rendered again
    expect(renderers.docxRendered).toBe(1);
    expect(Array.from(doc.querySelectorAll('.src-docx-cited')).map((p) => p.textContent)).toEqual([LINES[8]]);
    expect(Array.from(doc.querySelectorAll('[aria-current="location"]')).map((p) => p.textContent)).toEqual([LINES[8]]);
    expect(hits(doc, 'mark.src-hit-related')).toEqual([LINES[3]]);
    expect(await screen.findByText(/the cited passage is highlighted/)).toBeTruthy();
  });

  test('stepping to a passage in another document opens that document at its passage', async () => {
    api.getById.mockImplementation(async (id: string) =>
      id === OTHER_ID ? { ...otherSource(), originalDocument: { format: 'pdf', length: 9, pageLineStarts: [0], storedAt: '' } } : source('docx'),
    );
    render(<SourceDocumentViewer reference={invoices} passages={[invoices, reminders]} onClose={() => {}} />);
    await waitFor(() => expect(hits(frameDoc())).toEqual([LINES[3]]));

    fireEvent.click(screen.getByRole('button', { name: 'Next passage' }));

    await waitFor(() => expect(document.querySelector('.src-pdf')).not.toBeNull());
    expect(document.querySelector('iframe.src-docx')).toBeNull();
    expect(api.getById).toHaveBeenCalledWith(OTHER_ID, 'test-token');
  });

  test('pdf: zoom draws the pages larger and keeps the passage highlighted; Ctrl+0 goes back', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    card('listed here', [popup]);
    openLink(/^listed: open/);
    await screen.findByText(/Page 3: the cited passage is highlighted/);
    const page = () => document.querySelector('.src-pdf-page[data-page="3"]') as HTMLElement;
    expect(page().style.width).toBe('600px');
    expect(screen.getByLabelText('Zoom 100%')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Zoom in' }));
    fireEvent.click(screen.getByRole('button', { name: 'Zoom in' }));

    await waitFor(() => expect(page().style.width).toBe('750px'));
    expect(screen.getByLabelText('Zoom 125%')).toBeTruthy();
    await waitFor(() => expect(pageMarks(3)).toEqual(['listed in the decline popup']));
    expect(renderers.pdfScales.at(-1)).toBe(1.25);
    expect(renderers.pdfOpened).toBe(1); // the PDF is drawn again, not parsed again

    fireEvent.keyDown(pdfPane(), { key: '0', ctrlKey: true });
    await waitFor(() => expect(page().style.width).toBe('600px'));
    fireEvent.keyDown(pdfPane(), { key: '-', ctrlKey: true });
    await waitFor(() => expect(screen.getByLabelText('Zoom 90%')).toBeTruthy());
    // A zoom is not a new navigation: the note stays as it was.
    expect(screen.getByText(/Page 3: the cited passage is highlighted/)).toBeTruthy();
  });

  test('docx: zoom scales the rendered document and keeps its highlight', async () => {
    api.getById.mockResolvedValue(source('docx'));
    card('listed here', [popup]);
    openLink(/^listed: open/);
    await waitFor(() => expect(hits(frameDoc())).toEqual(['listed in the decline popup']));
    const zoomOf = () => frameDoc().documentElement.style.getPropertyValue('--src-zoom');
    expect(zoomOf()).toBe('1');

    fireEvent.click(screen.getByRole('button', { name: 'Zoom in' }));
    await waitFor(() => expect(zoomOf()).toBe('1.1'));
    fireEvent.click(screen.getByRole('button', { name: 'Zoom out' }));
    fireEvent.click(screen.getByRole('button', { name: 'Zoom out' }));
    await waitFor(() => expect(zoomOf()).toBe('0.9'));
    fireEvent.click(screen.getByRole('button', { name: 'Reset zoom' }));
    await waitFor(() => expect(zoomOf()).toBe('1'));

    expect(hits(frameDoc())).toEqual(['listed in the decline popup']);
    expect(renderers.docxRendered).toBe(1);
  });

  test('the zoom stops at its smallest and largest sizes', async () => {
    const { ZOOM_LEVELS, zoomForKey, zoomStep } = await import('../../src/lib/documentZoom');
    expect(zoomStep(1, 1)).toBe(1.1);
    expect(zoomStep(1, -1)).toBe(0.9);
    expect(zoomStep(ZOOM_LEVELS[0], -1)).toBe(ZOOM_LEVELS[0]);
    expect(zoomStep(ZOOM_LEVELS[ZOOM_LEVELS.length - 1], 1)).toBe(ZOOM_LEVELS[ZOOM_LEVELS.length - 1]);
    expect(zoomStep(1.2, 1)).toBe(1.25); // from between two levels, to the next one
    expect(zoomForKey(1.5, { key: '0', ctrlKey: true, metaKey: false, altKey: false })).toBe(1);
    expect(zoomForKey(1, { key: '=', ctrlKey: false, metaKey: true, altKey: false })).toBe(1.1);
    expect(zoomForKey(1, { key: '+', ctrlKey: false, metaKey: false, altKey: false })).toBeNull(); // typing, not zooming
  });

  test('the original is what opens - never the extracted text - for both PDF and Word', async () => {
    for (const format of ['pdf', 'docx'] as const) {
      api.getById.mockResolvedValue(source(format));
      const { unmount } = card('listed here', [popup]);
      openLink(/^listed: open/);
      await screen.findByText(/the cited passage is highlighted/);
      expect(document.querySelector(format === 'pdf' ? '.src-pdf' : 'iframe.src-docx')).not.toBeNull();
      expect(document.querySelector('.src-viewer-doc')).toBeNull();
      expect(screen.getByRole('toolbar', { name: `${format === 'pdf' ? 'PDF' : 'Word document'} controls` })).toBeTruthy();
      unmount();
    }
  });
});
