// Opening a CQ's source link (ClarificationQuestionCard -> SourceDocumentViewer): the modal opens over
// the same route, fetches through the (mocked) authorized API, shows the original PDF at the cited page
// or the original DOCX at the cited paragraphs with the heading or quote marked, falls back to the
// extracted text with a reason, and on close returns focus to the link without moving the page.
// The document renderers are replaced by fakes that build the text layer / paragraphs from the source
// text, so what is tested is the navigation and highlighting, not pdf.js or docx-preview themselves.
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
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

const renderers = vi.hoisted(() => ({ failPdf: false, pdfRendered: [] as number[] }));
vi.mock('../../src/lib/documentRenderers', () => ({
  // Each page's text layer holds that page's lines as spans, like pdf.js's.
  openPdf: async () => {
    if (renderers.failPdf) throw new Error('bad pdf');
    return {
      pageCount: PAGE_STARTS.length,
      pageSize: async () => ({ width: 600, height: 800 }),
      renderPage: async (n: number, container: HTMLElement) => {
        renderers.pdfRendered.push(n);
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
    const article = body.ownerDocument.createElement('article');
    for (const line of ['Cover', ...LINES]) {
      const p = body.ownerDocument.createElement('p');
      p.textContent = line;
      article.append(p);
    }
    body.replaceChildren(article);
    const paragraphs = Array.from(article.querySelectorAll('p')).map((element) => ({ element, text: element.textContent ?? '' }));
    return { blocks: paragraphs, paragraphs };
  },
}));

import { ClarificationQuestionCard } from '../../src/components/ClarificationQuestionCard';

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

function card(reason: string, references: SourceReference[]) {
  const artifact = {
    id: 'a1',
    code: 'CQ-004',
    data: { question: 'Which decline categories apply?', reason, clarificationStatus: 'Open', sourceReferences: references },
  } as unknown as ArtifactSummary;
  return render(
    <div>
      <ul>
        <ClarificationQuestionCard artifact={artifact} canAnswer={false} onAnswer={async () => {}} />
      </ul>
    </div>,
  );
}

const reason = 'The categories are only named in Section 12.7, and §3.2 does not say who may decline.';
const twelve = ref({ start: reason.indexOf('Section 12.7') });
const threeTwo = ref({ label: '§3.2', start: reason.indexOf('§3.2'), sectionNumber: '3.2', heading: '3.2 User Roles', line: 4, endLine: 6, anchor: 'L4', excerpt: null });

beforeEach(() => {
  renderers.failPdf = false;
  renderers.pdfRendered = [];
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
    expect(marks).toContain('listed in the decline popup');
    expect(marks).toContain('12.7 Decline Categories'); // its heading, on the same page
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
    expect(screen.getByText('Which decline categories apply?')).toBeTruthy(); // the same CQ, still open
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

  test('a missing original falls back to the extracted text and says why', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    api.getOriginal.mockRejectedValue(new ApiError(404, '{"type":"urn:aireap:original-unavailable"}'));
    card(reason, [twelve]);

    openLink(/^Section 12\.7: open/);

    expect(await screen.findByText(/The original file is no longer available, so its extracted text is shown/)).toBeTruthy();
    expect(document.querySelector('.src-line-heading')!.textContent).toContain('12.7 Decline Categories');
    expect(screen.queryByRole('button', { name: /Attach the original/ })).toBeNull(); // not a writer
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

  test('a PDF that cannot be rendered falls back to the extracted text', async () => {
    renderers.failPdf = true;
    api.getById.mockResolvedValue(source('pdf'));
    card(reason, [twelve]);

    openLink(/^Section 12\.7: open/);

    expect(await screen.findByText(/The original could not be displayed, so its extracted text is shown/)).toBeTruthy();
    expect(document.querySelector('.src-line-cited')).not.toBeNull();
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
  test('an ambiguous statement: "Why it matters" links to it, and the original PDF highlights exactly it', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    cardWith({ kind: 'ambiguous', reason: 'The popup is not described.', impact: 'Decides what the popup offers.', sourceEvidence: [decline], sourceLinkStatus: 'passage' });

    expect(screen.getByText('The ambiguous statement')).toBeTruthy();
    openLink(/^Source passage: open the requirement source at §12\.7/);

    const dialog = await screen.findByRole('dialog', { name: 'Triage SRS.pdf' });
    await screen.findByText(/Page 3: the cited passage is highlighted/);
    // The statement itself is the place navigated to; its heading is marked too, for context.
    expect(dialog.querySelector('.src-pdf-page[data-page="3"] mark.src-hit[aria-current="location"]')!.textContent).toBe(LINES[10]);
    expect(dialog.querySelector('.src-viewer-evidence')!.textContent).toContain('Ambiguous statement');
  });

  test('in the original DOCX only the quoted paragraph is cited, with its section heading marked apart', async () => {
    api.getById.mockResolvedValue(source('docx'));
    cardWith({ kind: 'ambiguous', reason: 'r', impact: 'i', sourceEvidence: [decline] });

    openLink(/^The eight decline categories.*: open/);

    await screen.findByText(/the cited passage is highlighted/);
    const doc = (document.querySelector('iframe.src-docx') as HTMLIFrameElement).contentDocument!;
    expect(Array.from(doc.querySelectorAll('.src-docx-cited')).map((p) => p.textContent)).toEqual([LINES[10]]);
    const heading = doc.querySelector('.src-docx-heading')!;
    expect(heading.textContent).toBe('12.7 Decline Categories');
    expect(heading.classList.contains('src-docx-cited')).toBe(false);
    expect(doc.querySelector('mark.src-hit')!.textContent).toBe(LINES[10]);
  });

  test('a conflict: both statements can be opened from one viewer, and the other is marked in the text', async () => {
    api.getById.mockResolvedValue(source(null));
    cardWith({ kind: 'contradiction', reason: 'r', impact: 'i', sourceEvidence: [invoices, passwords] });

    expect(screen.getByText('The conflicting statements')).toBeTruthy();
    openLink(/^Invoices are sent monthly\.: open/);

    await screen.findByRole('group', { name: 'Passages of this question' });
    await waitFor(() => expect(document.querySelector('.src-line-cited')?.getAttribute('data-line')).toBe('3'));
    expect(document.querySelector('.src-line-related')!.getAttribute('data-line')).toBe('8');
    expect(document.querySelector('.src-viewer-evidence')!.textContent).toContain('Conflicting statement');

    fireEvent.click(screen.getByRole('button', { name: 'Passage 2 of 2 (§2)' }));

    expect(document.querySelector('.src-line-cited')!.getAttribute('data-line')).toBe('8');
    expect(document.querySelector('.src-line-related')!.getAttribute('data-line')).toBe('3');
    expect(document.querySelector('.src-line-cited mark')!.textContent).toBe('Passwords are stored hashed.');
  });

  test('missing information is shown as the context of what is missing, never as the missing detail', async () => {
    api.getById.mockResolvedValue(source(null));
    cardWith({ kind: 'missing', reason: 'Who may decline is not stated.', impact: 'i', sourceEvidence: [decline] });

    expect(screen.getByText(/Context in the source — the information asked for is not written there/)).toBeTruthy();
    openLink(/^Source passage: open/);

    const label = await screen.findByText('Context for the missing information');
    expect(label.closest('.src-viewer-evidence')!.textContent).toContain(LINES[10]);
    expect(document.querySelector('.src-line-cited')!.getAttribute('data-line')).toBe('10');
  });

  test('an older question whose "Why it matters" names no place links to its recovered passage, and says how it was found', async () => {
    api.getById.mockResolvedValue(source(null));
    cardWith({
      reason: 'Reviewers cannot tell which categories apply.',
      sourceEvidence: [{ ...decline, recovery: 'generation-response' }],
      sourceLinkStatus: 'passage',
    });

    const why = screen.getByText('Why it matters:').closest('.cq-reason')!;
    expect(why.textContent).toContain('Source passage');
    expect(screen.getByText(/Linked from the passage the analysis quoted/)).toBeTruthy();

    openLink(/^Source passage: open/);
    await screen.findByRole('dialog');
    await waitFor(() => expect(document.querySelector('.src-line-cited')?.getAttribute('data-line')).toBe('10'));
  });

  test('repeated wording asks the reader to check the place, and unplaced evidence has no link', () => {
    cardWith({ kind: 'ambiguous', reason: 'r', impact: 'i', sourceEvidence: [{ ...decline, occurrences: 2 }, quoteAt(3, '1 Billing', '1', { line: -1, endLine: -1 })] });

    expect(screen.getByText(/These words appear 2 times in the source/)).toBeTruthy();
    expect(screen.getByText(/not located in the current source text/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^Invoices are sent monthly\.: open/ })).toBeNull();
  });

  test('an unresolved question says so, and browsing opens the whole source with nothing highlighted', async () => {
    api.getById.mockResolvedValue(source('pdf'));
    cardWith({ reason: 'Nothing in the source covers this.', sourceLinkStatus: 'unresolved' });

    expect(screen.getByRole('note').textContent).toContain('could be verified as the reason for this question');
    openLink(/^Browse the source$/);

    await screen.findByText(/nothing is highlighted\. The whole source is shown/);
    await waitFor(() => expect(renderers.pdfRendered).toEqual([1]));
    expect(document.querySelector('mark, .src-line-cited')).toBeNull();
    expect(screen.queryByText(/could not be located in this source/)).toBeNull();
  });
});
