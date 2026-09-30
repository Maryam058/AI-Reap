import type { ArtifactSummary, TraceabilityRow, TraceRef } from '../api/types';
import { BUCKET_LABEL, BUCKET_ORDER, countBuckets } from './requirements';

/**
 * Presentation-only grouping of generated artifacts. Every grouping here is derived from data the API
 * already returns (traceability links, payload fields); nothing is inferred or invented.
 */
export interface ArtifactGroup {
  key: string;
  label: string;
  /** Short identifier shown before the label, e.g. a requirement code. */
  code?: string;
  items: ArtifactSummary[];
  /** The functional requirement this group belongs to, when grouped by requirement. */
  requirementId?: string;
}

type LinkField = 'businessRules' | 'userStories' | 'acceptanceCriteria' | 'implementationTasks' | 'testCases';

export const UNLINKED_KEY = 'unlinked';

/**
 * Groups items under the functional requirement the traceability matrix links them to, in requirement order.
 * An item linked to several requirements is listed under the first one, so counts add up to the total.
 * Returns null when the matrix is unavailable, so callers fall back to a flat list.
 */
export function groupByRequirement(
  items: ArtifactSummary[],
  requirements: ArtifactSummary[],
  matrix: TraceabilityRow[] | null,
  field: LinkField,
  { includeEmpty = false }: { includeEmpty?: boolean } = {},
): ArtifactGroup[] | null {
  if (!matrix) return null;
  const rows = new Map(matrix.map((r) => [r.functionalRequirementId, r]));
  const byId = new Map(items.map((a) => [a.id, a]));
  const placed = new Set<string>();
  const groups: ArtifactGroup[] = [];

  for (const fr of requirements) {
    const refs: TraceRef[] = rows.get(fr.id)?.[field] ?? [];
    const members: ArtifactSummary[] = [];
    for (const ref of refs) {
      const item = byId.get(ref.id);
      if (item && !placed.has(item.id)) {
        placed.add(item.id);
        members.push(item);
      }
    }
    if (members.length > 0 || includeEmpty) {
      groups.push({ key: fr.id, code: fr.code, label: fr.title, items: members, requirementId: fr.id });
    }
  }

  const unlinked = items.filter((a) => !placed.has(a.id));
  if (unlinked.length > 0) groups.push({ key: UNLINKED_KEY, label: 'Not linked to a requirement', items: unlinked });
  // Nothing is linked: a single catch-all group adds no information.
  if (groups.length === 0 || (groups.length === 1 && groups[0].key === UNLINKED_KEY)) return null;
  return groups;
}

/**
 * Groups items by a payload value, in first-appearance order. Returns null when grouping adds nothing:
 * a single group, or one group per item.
 */
export function groupByValue(items: ArtifactSummary[], valueOf: (a: ArtifactSummary) => string | null | undefined, fallback: string): ArtifactGroup[] | null {
  const groups = new Map<string, ArtifactGroup>();
  for (const a of items) {
    const raw = valueOf(a)?.trim();
    const label = raw ? raw : fallback;
    const key = label.toLowerCase();
    if (!groups.has(key)) groups.set(key, { key, label, items: [] });
    groups.get(key)!.items.push(a);
  }
  return groups.size > 1 && groups.size < items.length ? [...groups.values()] : null;
}

const dataString = (a: ArtifactSummary, field: string) => {
  const v = a.data[field];
  return typeof v === 'string' ? v : null;
};

/** The payload field `field` as a string, for groupByValue. */
export const byField = (field: string) => (a: ArtifactSummary) => dataString(a, field);

const ROUTE_NOISE = /^(api|v\d+)$/i;

/** First meaningful path segment of an API specification's route, e.g. "/api/v1/users/{id}" → "/users". */
export function apiResourceOf(a: ArtifactSummary): string | null {
  const route = dataString(a, 'route');
  if (!route) return null;
  const segment = route
    .split('?')[0]
    .split('/')
    .map((s) => s.trim())
    .find((s) => s && !ROUTE_NOISE.test(s) && !s.startsWith('{') && !s.startsWith(':'));
  return segment ? `/${segment}` : null;
}

/** Compact review-status summary, e.g. "2 in review · 3 approved". Empty buckets are omitted. */
export function statusSummary(items: ArtifactSummary[]): string {
  const counts = countBuckets(items);
  return BUCKET_ORDER.filter((b) => counts[b] > 0)
    .map((b) => `${counts[b]} ${BUCKET_LABEL[b].toLowerCase()}`)
    .join(' · ');
}

export const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;
