import type { TraceRef, TraceabilityGaps, TraceabilityOrphan, TraceabilityRow } from '../api/types';

// ArtifactType.UserStory (ARTIFACT_TYPE_VALUES): a value import would keep this file from running under node --test.
const USER_STORY = 3;

/**
 * Matrix-shaped rows for the non-functional requirements (which have no matrix rows), carrying only their test
 * cases, so test cases can be grouped under the NFR they verify with the same grouping as FR test cases.
 */
export function nfrTestRows(gaps: TraceabilityGaps | null): TraceabilityRow[] {
  return (gaps?.nonFunctionalTestCoverage ?? []).map((n) => ({
    functionalRequirementId: n.id,
    functionalRequirementCode: n.code,
    functionalRequirementTitle: n.title,
    businessObjectives: [],
    businessRules: [],
    userStories: [],
    acceptanceCriteria: [],
    designArtifacts: [],
    dataEntities: [],
    apiSpecifications: [],
    implementationTasks: [],
    testCases: n.testCases,
  }));
}

/** The orphans among the given artifacts (e.g. one source's), for a "not linked to a requirement" notice. */
export function orphansAmong(gaps: TraceabilityGaps | null, artifactIds: Iterable<string>): TraceabilityOrphan[] {
  const ids = new Set(artifactIds);
  return (gaps?.orphans ?? []).filter((o) => ids.has(o.id));
}

/** A functional requirement nothing has been built or tested from yet: no story, no task or no test case. */
export function rowHasGap(row: TraceabilityRow): boolean {
  return row.userStories.length === 0 || row.implementationTasks.length === 0 || row.testCases.length === 0;
}

export interface GapSummary {
  withoutStories: TraceRef[];
  withoutTests: TraceRef[];
  /** Orphans grouped by artifact type, in type order. */
  orphansByType: [number, TraceabilityOrphan[]][];
  untracedNfrs: TraceRef[];
  nfrCount: number;
  total: number;
}

/**
 * The coverage gaps shown above the matrix: FR rows with no story or no test (from the matrix itself), and what
 * the matrix cannot show - downstream work linked to no requirement, and NFRs nothing traces to (from the
 * traceability-gaps endpoint; `gaps` is null when that has not loaded).
 */
export function gapsOf(rows: TraceabilityRow[], gaps: TraceabilityGaps | null): GapSummary {
  const ref = (r: TraceabilityRow): TraceRef => ({ id: r.functionalRequirementId, code: r.functionalRequirementCode, title: r.functionalRequirementTitle });
  const withoutStories = rows.filter((r) => r.userStories.length === 0).map(ref);
  const withoutTests = rows.filter((r) => r.testCases.length === 0).map(ref);

  const byType = new Map<number, TraceabilityOrphan[]>();
  for (const orphan of gaps?.orphans ?? []) {
    byType.set(orphan.artifactType, [...(byType.get(orphan.artifactType) ?? []), orphan]);
  }
  const orphansByType = [...byType.entries()].sort(([a], [b]) => a - b);
  const untracedNfrs = gaps?.untracedNonFunctionalRequirements ?? [];

  return {
    withoutStories,
    withoutTests,
    orphansByType,
    untracedNfrs,
    nfrCount: gaps?.nonFunctionalRequirementCount ?? 0,
    total: withoutStories.length + withoutTests.length + (gaps?.orphans.length ?? 0) + untracedNfrs.length,
  };
}

/** What an orphan can be linked to: a story for an acceptance criterion, else a functional or non-functional requirement. */
// `rejected`: ids of rejected artifacts, which the server refuses as link targets (409), so they are not offered.
export function linkCandidates(
  orphan: TraceabilityOrphan,
  rows: TraceabilityRow[],
  gaps: TraceabilityGaps | null,
  rejected: ReadonlySet<string> = new Set(),
): TraceRef[] {
  const live = (r: TraceRef) => !rejected.has(r.id);
  if (orphan.missingLink === 'user story') {
    const stories = new Map<string, TraceRef>();
    rows.forEach((r) => r.userStories.forEach((s) => stories.set(s.id, s)));
    (gaps?.orphans ?? []).filter((o) => o.artifactType === USER_STORY).forEach((o) => stories.set(o.id, o));
    return [...stories.values()].filter(live).sort((a, b) => a.code.localeCompare(b.code));
  }
  const requirements: TraceRef[] = rows.map((r) => ({ id: r.functionalRequirementId, code: r.functionalRequirementCode, title: r.functionalRequirementTitle }));
  return [...requirements, ...(gaps?.nonFunctionalTestCoverage ?? [])].filter(live);
}
