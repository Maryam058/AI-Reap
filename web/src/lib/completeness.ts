// P0-7 — the workspace notice for how complete an Analyze or Generate result is (its `completeness`,
// response-only). Pure, so the wording rules are testable:
// - a split source is described as it is: every passage was sent once, but the AI could not relate
//   statements in different parts - never as missing content;
// - clarifications are whole, distributed (each part the most relevant ones) or absent;
// - evidence counts cover only the requirements this run created; quotes are described as "found in
//   the SRS" / "not found as written", never as a verified requirement;
// - source-ID coverage is the result's sourceIdCoverage, read through coverageNotice (P0-4) - not
//   recomputed; missing or not-applicable coverage is never treated as complete;
// - "success" only when the source went whole, coverage is explicitly complete, all three evidence
//   counts are zero and the run created at least one requirement.
// An older API without `completeness` shows nothing.
import type { AnalyzeRequirementResult, GenerateRequirementsResult, RequirementCompleteness } from '../api/types.ts';
import { coverageNotice } from './sourceIdCoverage.ts';

export type CompletenessTone = 'warning' | 'info' | 'success';

export interface CompletenessNotice {
  tone: CompletenessTone;
  title: string;
  details: string[];
}

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

function processingLine(c: RequirementCompleteness, verb: string): string {
  return c.contextReduction === 'split'
    ? `${verb} in ${c.partCount} parts: every passage of the source was sent once, but the AI could not relate statements in different parts.`
    : `${verb} whole, in one request.`;
}

function clarificationsLine(c: RequirementCompleteness): string | null {
  switch (c.clarifications) {
    case 'distributed':
      return 'Each part carried the clarifications most relevant to it; every clarification went with at least one part.';
    case 'whole':
      return c.contextReduction === 'split' ? 'Every part carried all clarifications.' : 'All clarifications were sent with the source.';
    default:
      return null;
  }
}

function modelsLine(c: RequirementCompleteness): string | null {
  return c.models.length > 0 ? `Served by ${c.models.join(', ')}.` : null;
}

export function analysisCompletenessNotice(result: AnalyzeRequirementResult | null | undefined): CompletenessNotice | null {
  const c = result?.completeness;
  if (!c) return null;
  return {
    tone: 'info',
    title: c.contextReduction === 'split' ? `Source analysed in ${c.partCount} parts` : 'Source analysed whole',
    details: [processingLine(c, 'Analysed'), modelsLine(c)].filter((l): l is string => l !== null),
  };
}

export function generationCompletenessNotice(result: GenerateRequirementsResult | null | undefined): CompletenessNotice | null {
  const c = result?.completeness;
  if (!result || !c) return null;

  const created = result.functionalRequirements.length + result.nonFunctionalRequirements.length;
  const unverified = c.unverifiedEvidenceCount ?? 0;
  const noEvidence = c.noEvidenceCount ?? 0;
  const numbers = c.unsupportedNumbersCount ?? 0;
  const countsReported = c.unverifiedEvidenceCount != null && c.noEvidenceCount != null && c.unsupportedNumbersCount != null;

  const evidence: string[] = [];
  if (unverified > 0) evidence.push(`${plural(unverified, 'new requirement has', 'new requirements have')} a quote that was not found as written in the SRS.`);
  if (noEvidence > 0) evidence.push(`${plural(noEvidence, 'new requirement quotes', 'new requirements quote')} no evidence.`);
  if (numbers > 0) evidence.push(`${plural(numbers, 'new requirement states', 'new requirements state')} a number that none of its quotes contains.`);

  const coverage = result.sourceIdCoverage;
  const coverageComplete = coverage?.status === 'complete';
  const coverageLine = coverageComplete
    ? null
    : coverage
      ? `${coverageNotice(coverage)!.title}.`
      : 'Requirement ID coverage was not reported.';

  const skipped = result.skippedDuplicates ?? 0;
  const savedLine =
    created === 0
      ? `No new requirements were saved${skipped > 0 ? `; ${plural(skipped, 'requirement', 'requirements')} already existed` : ''}.`
      : null;

  const details = [processingLine(c, 'Generated'), clarificationsLine(c), savedLine, ...evidence, coverageLine, modelsLine(c)]
    .filter((l): l is string => l !== null);

  const success = c.contextReduction === 'none' && coverageComplete && countsReported && evidence.length === 0 && created > 0;
  if (success) {
    return {
      tone: 'success',
      title: 'Source processed whole, every requirement ID cited, and a quote found in SRS for every new requirement',
      details,
    };
  }

  const warning = evidence.length > 0 || coverage?.status === 'incomplete';
  return {
    tone: warning ? 'warning' : 'info',
    title: warning ? 'Check this result before relying on it' : 'Result generated with limitations',
    details,
  };
}
