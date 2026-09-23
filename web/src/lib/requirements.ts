import { ARTIFACT_TYPE_VALUES, type ArtifactSummary } from '../api/types';

export type RequirementBucket = 'draft' | 'review' | 'approved' | 'completed' | 'rejected';

export const BUCKET_LABEL: Record<RequirementBucket, string> = {
  draft: 'Draft',
  review: 'In review',
  approved: 'Approved',
  completed: 'Completed',
  rejected: 'Rejected',
};

export const BUCKET_COLOR: Record<RequirementBucket, string> = {
  draft: 'var(--chart-neutral)',
  review: 'var(--chart-warning)',
  approved: 'var(--chart-1)',
  completed: 'var(--chart-success)',
  rejected: 'var(--chart-danger)',
};

export const BUCKET_ORDER: RequirementBucket[] = ['draft', 'review', 'approved', 'completed', 'rejected'];

/** Maps the backend artifact status (0 AI Generated … 6 Verified) onto the buckets shown in charts. */
export function bucketOf(status: number): RequirementBucket {
  switch (status) {
    case 2:
      return 'review';
    case 3:
      return 'approved';
    case 5:
    case 6:
      return 'completed';
    case 4:
      return 'rejected';
    default:
      return 'draft';
  }
}

export const isRequirement = (a: ArtifactSummary) =>
  a.artifactType === ARTIFACT_TYPE_VALUES.FunctionalRequirement || a.artifactType === ARTIFACT_TYPE_VALUES.NonFunctionalRequirement;

export function countBuckets(items: ArtifactSummary[]): Record<RequirementBucket, number> {
  const out: Record<RequirementBucket, number> = { draft: 0, review: 0, approved: 0, completed: 0, rejected: 0 };
  for (const a of items) out[bucketOf(a.status)]++;
  return out;
}

/** Cumulative count of items by creation week (ISO week start, Monday). Only real createdAt values are used. */
export function weeklyCumulative(items: ArtifactSummary[]): { label: string; value: number }[] {
  if (items.length === 0) return [];
  const weekStart = (iso: string) => {
    const d = new Date(iso);
    const day = (d.getUTCDay() + 6) % 7;
    return Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate() - day);
  };
  const perWeek = new Map<number, number>();
  for (const a of items) {
    const w = weekStart(a.createdAt);
    if (Number.isNaN(w)) continue;
    perWeek.set(w, (perWeek.get(w) ?? 0) + 1);
  }
  const weeks = [...perWeek.keys()].sort((a, b) => a - b);
  if (weeks.length === 0) return [];
  const WEEK = 7 * 24 * 3600 * 1000;
  const out: { label: string; value: number }[] = [];
  let total = 0;
  for (let w = weeks[0]; w <= weeks[weeks.length - 1]; w += WEEK) {
    total += perWeek.get(w) ?? 0;
    out.push({ label: new Date(w).toLocaleDateString(undefined, { month: 'short', day: 'numeric', timeZone: 'UTC' }), value: total });
  }
  return out;
}
