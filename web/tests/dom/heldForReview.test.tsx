// The Held for review section (HeldForReviewSection over lib/heldForReview): each held item shows the
// question as generated, the cited passage, the contradicting passage and why it was held, read-only and
// marked apart from clarification questions; the empty state; long passages; opening a passage in the
// (mocked) source viewer. Sample data only; nothing here reaches a server.
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, test, vi } from 'vitest';
import type { AnalyzeRequirementResult, HeldClarification, RequirementSource } from '../../src/api/types';
import { HeldForReviewSection } from '../../src/components/HeldForReviewSection';
import { heldForReviewItems } from '../../src/lib/heldForReview';

const SOURCE_ID = '0d3f7a52-3b8e-4c55-9a1e-6f2d4b8c9e01';
const LINES = [
  'Expense Claims Module',
  '3.2 Claim Approval',
  'Claims over 500 are approved by a manager.',
  'The approval step cannot be turned off.',
];

const api = vi.hoisted(() => ({ getById: vi.fn(), getOriginal: vi.fn(), attachOriginal: vi.fn() }));
vi.mock('../../src/api/requirementSources', () => ({ requirementSourcesApi: api }));
vi.mock('../../src/auth/AuthContext', () => ({ useAuth: () => ({ token: 'test-token', hasRole: () => false }) }));

const base: AnalyzeRequirementResult = { actors: [], capabilities: [], dataElements: [], notes: [], clarificationQuestions: [] };
const QUESTION = 'Which manager approves claims once the approval step is turned off?';
const held = (over: Partial<HeldClarification> = {}): HeldClarification => ({
  topic: 'Claim approval',
  question: QUESTION,
  kind: 'missing',
  evidence: [
    { label: 'q', start: 0, sourceId: SOURCE_ID, heading: '3.2 Claim Approval', line: 2, endLine: 3, anchor: 'L2', excerpt: LINES[2], excerptIsQuote: true },
  ],
  denialLine: 3,
  denialText: LINES[3],
  location: 'line 4',
  note: 'Not asked as written: it assumes something the source denies at line 4. A reviewer should decide whether to ask it neutrally.',
  ...over,
});

const show = (heldForReview: HeldClarification[] | null | undefined) => {
  const analysis = heldForReview === undefined ? base : { ...base, heldForReview };
  return render(<HeldForReviewSection items={heldForReviewItems(analysis, SOURCE_ID)} />);
};

afterEach(cleanup);

describe('Held for review', () => {
  test('one held item: question verbatim, both passages, reason, needs-review status, no actions', () => {
    show([held()]);
    const section = document.getElementById('sec-held-for-review')!;
    expect(within(section).getByRole('heading', { name: /Held for review/ })).toBeTruthy();
    const card = within(section).getAllByRole('listitem')[0];
    const c = within(card);
    expect(c.getByText(QUESTION)).toBeTruthy();
    expect(c.getByText('Needs human review')).toBeTruthy();
    expect(c.getByText('Not a clarification question')).toBeTruthy();
    expect(within(c.getByRole('region', { name: 'Supporting evidence from the document' })).getByText(LINES[2])).toBeTruthy();
    expect(within(c.getByRole('region', { name: 'Passage that contradicts the assumption' })).getByText(LINES[3])).toBeTruthy();
    expect(c.getByText(held().note)).toBeTruthy();
    // Read-only: only passage links, no answer/approve/reject/edit/regenerate controls.
    const buttons = c.getAllByRole('button').map((b) => b.getAttribute('aria-label') ?? b.textContent);
    expect(buttons).toEqual(['Open the requirement source at 3.2 Claim Approval', 'Open the requirement source at Line 4']);
    expect(c.queryByRole('textbox')).toBeNull();
    // No internal codes or JSON.
    expect(card.textContent).not.toMatch(/unsupported-assumption|denialLine|\{"/);
  });

  test('multiple held items render as separate cards', () => {
    show([held(), held({ question: 'Who signs off a claim under 500?', denialText: 'Claims under 500 need no sign-off.', denialLine: 3, location: '§3.2' })]);
    const cards = within(document.getElementById('sec-held-for-review')!).getAllByRole('listitem');
    expect(cards).toHaveLength(2);
    expect(within(cards[0]).getByText(QUESTION)).toBeTruthy();
    expect(within(cards[1]).getByText('Who signs off a claim under 500?')).toBeTruthy();
    expect(within(cards[1]).getByText('Claims under 500 need no sign-off.')).toBeTruthy();
    expect(within(cards[0]).queryByText('Claims under 500 need no sign-off.')).toBeNull();
  });

  test('empty state when nothing is held, and for older analyses without the field', () => {
    for (const value of [[], null, undefined] as const) {
      show(value as HeldClarification[] | null | undefined);
      const section = document.getElementById('sec-held-for-review')!;
      expect(within(section).getByText('Nothing held for review')).toBeTruthy();
      expect(within(section).queryAllByRole('listitem')).toHaveLength(0);
      cleanup();
    }
  });

  test('a long passage is shortened with Show more and expands in full', () => {
    const long = `${'The approval step applies to every claim submitted through the portal and to claims entered by finance staff. '.repeat(8)}End.`;
    show([held({ denialText: long })]);
    const region = screen.getByRole('region', { name: 'Passage that contradicts the assumption' });
    expect(region.textContent).not.toContain('End.');
    fireEvent.click(within(region).getByRole('button', { name: 'Show more' }));
    expect(region.textContent).toContain('End.');
    expect(within(region).getByRole('button', { name: 'Show less' })).toBeTruthy();
  });

  test('opening the contradicting passage shows the source viewer with the question', async () => {
    const source = { id: SOURCE_ID, projectId: 'p', title: 'Expense claims', rawText: LINES.join('\n'), createdAt: '2026-11-19T00:00:00Z' } as unknown as RequirementSource;
    api.getById.mockResolvedValue(source);
    show([held()]);
    fireEvent.click(screen.getByRole('button', { name: 'Open the requirement source at Line 4' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getAllByText(QUESTION).length).toBeGreaterThan(0);
    await waitFor(() => expect(api.getById).toHaveBeenCalledWith(SOURCE_ID, 'test-token'));
  });
});
