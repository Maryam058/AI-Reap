import type { TraceabilityRow } from '../api/types';

/**
 * Test coverage means one thing everywhere in this app: the share of functional requirements
 * that have at least one linked test case, per the traceability matrix - not a raw ratio of
 * total test-case count to total requirement count (which can wrongly read as >100% covered
 * when a handful of requirements each have many tests while others have none).
 */
export function testCoveragePercent(rows: TraceabilityRow[]): number {
  if (rows.length === 0) return 0;
  const covered = rows.filter((r) => r.testCases.length > 0).length;
  return Math.round((covered / rows.length) * 100);
}
