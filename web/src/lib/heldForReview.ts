// Proposed clarification questions the server held for a reviewer (AnalyzeRequirementResult.heldForReview,
// ClarificationCandidateValidator.HeldForReview): each assumes something another passage of the source
// denies, and no saved question covers that. Read-only: this only reshapes the API's own data for
// HeldForReviewSection - nothing is rewritten, answered or turned into a question. Pure, so testable.
import type { AnalyzeRequirementResult, HeldClarification, SourceReference } from '../api/types.ts';
import { clarificationKindLabel, isLocatedEvidence, sourceLinkLabel } from './sourceReferences.ts';

export interface HeldPassage {
  // The passage's words, exactly as the server returned them (null when it sent none).
  text: string | null;
  // Where it is, for a person: the section heading, the server's location, or the line.
  where: string;
  // The place to open in the source viewer; null when the passage was not placed in the source.
  reference: SourceReference | null;
}

export interface HeldItem {
  key: string;
  topic: string;
  // The question exactly as generated.
  question: string;
  kindLabel: string | null;
  evidence: HeldPassage[];
  contradicting: HeldPassage;
  // Why it was held, as the server wrote it.
  reason: string;
}

const isIndex = (n: unknown): n is number => typeof n === 'number' && Number.isInteger(n) && n >= 0;
const text = (s: unknown): string | null => (typeof s === 'string' && s.trim().length > 0 ? s : null);

// The held items of an analysis. A missing or null field (analyses made before held items were kept)
// is an empty list; an entry without a question is skipped, since there is nothing to review.
export function heldForReviewItems(result: AnalyzeRequirementResult | null | undefined, sourceId: string): HeldItem[] {
  const held: unknown = result?.heldForReview;
  if (!Array.isArray(held)) return [];
  return held.flatMap((entry: Partial<HeldClarification> | null, i): HeldItem[] => {
    const question = text(entry?.question);
    if (!entry || !question) return [];
    const evidence = (Array.isArray(entry.evidence) ? entry.evidence : []).map(evidencePassage);
    return [
      {
        key: `${i}-${question}`,
        topic: text(entry.topic) ?? '',
        question,
        kindLabel: text(entry.kind) ? clarificationKindLabel(entry.kind as string) : null,
        evidence,
        contradicting: contradictingPassage(entry, sourceId || evidence.find((e) => e.reference)?.reference?.sourceId || ''),
        reason: text(entry.note) ?? 'It assumes something another passage of the source contradicts.',
      },
    ];
  });
}

function evidencePassage(ref: Partial<SourceReference> | null): HeldPassage {
  const located = isLocatedEvidence(ref) ? ref : null;
  const where = located ? sourceLinkLabel(located) : 'Not located in the source';
  return { text: text(ref?.excerpt), where: located && where === 'Source passage' ? `Line ${located.line + 1}` : where, reference: located };
}

// The passage that denies the question's assumption: one line of the source, opened like quoted evidence.
function contradictingPassage(entry: Partial<HeldClarification>, sourceId: string): HeldPassage {
  const quote = text(entry.denialText);
  const line = entry.denialLine;
  const location = text(entry.location);
  const where = location ? location.charAt(0).toUpperCase() + location.slice(1) : (isIndex(line) ? `Line ${line + 1}` : 'Not located in the source');
  const reference: SourceReference | null =
    isIndex(line) && quote && sourceId
      ? { label: where, start: 0, sourceId, sectionNumber: null, heading: null, line, endLine: line + 1, anchor: `L${line}`, excerpt: quote, excerptIsQuote: true }
      : null;
  return { text: quote, where, reference };
}
