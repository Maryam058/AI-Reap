// P0-2 — the labels ArtifactCard shows for a generated requirement's origin, evidence quotes and NFR
// target. Pure, so the wording rules are testable: "found" only ever means the quoted words were found
// in the cited text — no label says a requirement is correct or a target is confirmed — and only an
// SRS quote is ever labelled as found in the SRS.
import type { EvidenceQuote, NonFunctionalRequirementData, RequirementEvidenceData } from '../api/types';

export type EvidenceTone = 'success' | 'warning' | 'neutral';

export interface EvidenceLabel {
  text: string;
  tone: EvidenceTone;
}

const norm = (v: string | null | undefined) => (v ?? '').trim().toLowerCase();

// The model's classification as stored; an unrecognised value is shown as written, never reclassified.
export function originLabel(origin: string | null | undefined): EvidenceLabel | null {
  switch (norm(origin)) {
    case 'explicit':
      return { text: 'Explicit in source', tone: 'neutral' };
    case 'derived':
      return { text: 'Derived from source', tone: 'neutral' };
    case 'assumed':
      return { text: 'Assumed — not stated in source', tone: 'warning' };
    case '':
      return origin === undefined ? null : { text: 'Origin not stated', tone: 'warning' };
    default:
      return { text: `Origin (unrecognised): ${origin!.trim()}`, tone: 'warning' };
  }
}

// The item-level evidence summary. Records made before evidence checks have no evidenceCheck; as on the
// server (RequirementApprovalEvidence), a blank one counts as none and the status is compared trimmed
// and case-insensitively. An "unverified" status is shown as such whatever the check says, because the
// server blocks approval on it before looking at the check. A checked record whose status is missing or
// unrecognised is said to be just that - never verified, and never "no evidence quoted", which is a
// status of its own.
export function evidenceSummary(data: RequirementEvidenceData): EvidenceLabel {
  if (norm(data.evidenceStatus) === 'unverified') {
    return { text: 'A quote was not found as written', tone: 'warning' };
  }
  if (!norm(data.evidenceCheck)) {
    return { text: 'No evidence recorded (generated before evidence checks)', tone: 'neutral' };
  }
  switch (norm(data.evidenceStatus)) {
    case 'verified':
      return { text: 'Every quote found as written', tone: 'success' };
    case 'none':
      return { text: 'No evidence quoted', tone: 'warning' };
    default:
      return { text: 'Evidence status not recognised (not verified)', tone: 'warning' };
  }
}

const REASONS: Record<string, string> = {
  not_found: 'not found in the cited text',
  altered: 'not as written — the cited text has similar but different wording',
  too_short: 'too short to check',
  too_long: 'longer than 40 words',
  unknown_source_id: 'cited requirement ID is not in the source',
  not_at_cited_id: 'found, but not under the cited requirement ID',
  source_unavailable: 'the cited text is no longer available',
  unknown_reference: 'cites an unknown clarification or document',
  clarification_unanswered: 'the cited clarification is not answered yet',
  invalid_source_type: 'does not say where it comes from',
};

// Where a quote says it comes from, as a reader would name it.
function place(q: EvidenceQuote): string {
  switch (norm(q.from)) {
    case 'srs':
      return q.sourceId ? `SRS (${q.sourceId})` : 'SRS';
    case 'clarification':
      return q.ref ? `clarification ${q.ref}` : 'a clarification';
    case 'excerpt':
      return q.ref ? q.ref : 'a project document';
    default:
      return 'an unnamed source';
  }
}

export function quoteLabel(q: EvidenceQuote): EvidenceLabel {
  if (q.status === 'verified') {
    return { text: `Quote found in ${place(q)}`, tone: 'success' };
  }
  const reason = (q.reason && REASONS[q.reason]) || 'not checked';
  return { text: `Quote from ${place(q)}: ${reason}`, tone: 'warning' };
}

// An NFR target's origin and the server's support check. Every target still needs a human
// confirmation, said explicitly until it is confirmed.
export function targetLabel(data: NonFunctionalRequirementData): EvidenceLabel | null {
  if (!data.targetValue) return null;
  const confirmed = data.assumptionStatus === 'confirmed';
  switch (norm(data.targetOrigin)) {
    case 'stated':
      return data.targetSupport === 'quote_found'
        ? { text: `Stated in SRS — quote found${confirmed ? '' : ' — needs confirmation'}`, tone: confirmed ? 'success' : 'neutral' }
        : { text: 'Claimed stated — not found in quoted SRS text', tone: 'warning' };
    case 'proposed':
      return { text: 'AI-proposed target', tone: 'warning' };
    default:
      return null;
  }
}

// A warning, not a verdict: numbers in the requirement no verified quote contains.
export function unsupportedNumbersLabel(numbers: string[] | null | undefined): string | null {
  return numbers && numbers.length > 0 ? `Not in quoted evidence: ${numbers.join(', ')}` : null;
}
