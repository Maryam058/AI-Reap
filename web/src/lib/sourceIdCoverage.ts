// P0-4 — the workspace notice for a requirement source's ID coverage (GET .../source-id-coverage).
// Pure, so the wording rules are testable: an uncovered ID is a warning; IDs cited only by rejected
// requirements are named separately; a source without defined IDs says coverage cannot be checked
// rather than "complete"; identifiers only mentioned in the text are listed, never hidden.
import type { SourceIdCoverage } from '../api/types';

export type CoverageTone = 'warning' | 'info' | 'success';

export interface CoverageNotice {
  tone: CoverageTone;
  title: string;
  details: string[];
}

// At most `max` IDs, then "and N more".
export function listIds(ids: string[], max = 12): string {
  if (ids.length <= max) return ids.join(', ');
  return `${ids.slice(0, max).join(', ')} and ${ids.length - max} more`;
}

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

export function coverageNotice(coverage: SourceIdCoverage | null | undefined): CoverageNotice | null {
  if (!coverage) return null;
  const mentioned =
    coverage.mentionedOnly.length > 0
      ? [`Mentioned in the text but not written as requirements, so not checked: ${listIds(coverage.mentionedOnly)}`]
      : [];

  switch (coverage.status) {
    case 'not_applicable':
      return {
        tone: 'info',
        title: 'Requirement ID coverage cannot be checked',
        details: ['No requirement ID (such as FR-012) starts a line in this source.', ...mentioned],
      };
    case 'complete':
      return {
        tone: 'success',
        title: `Every requirement ID in the source is cited (${plural(coverage.definedCount, 'ID', 'IDs')})`,
        details: mentioned,
      };
    default: {
      const rejectedOnly = new Set(coverage.coveredOnlyByRejected.map((id) => id.toUpperCase()));
      const missing = coverage.uncovered.filter((id) => !rejectedOnly.has(id.toUpperCase()));
      return {
        tone: 'warning',
        title: `${coverage.uncovered.length} of ${plural(coverage.definedCount, 'requirement ID', 'requirement IDs')} in the source not covered by any requirement`,
        details: [
          ...(missing.length > 0 ? [`Cited by no requirement: ${listIds(missing)}`] : []),
          ...(coverage.coveredOnlyByRejected.length > 0
            ? [`Cited only by rejected requirements: ${listIds(coverage.coveredOnlyByRejected)}`]
            : []),
          ...mentioned,
        ],
      };
    }
  }
}
