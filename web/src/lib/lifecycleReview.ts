import { TIMELINE_EVENT_KIND, WORK_OUTCOME, type LifecycleReview, type ReviewedArtifact, type ReviewTimelineEvent } from '../api/types.ts';
import { stageLabel } from './lifecycle.ts';

/**
 * Display helpers for the lifecycle review (GET /api/projects/{id}/lifecycle-review). The backend
 * decides every stage attribution; nothing here infers one. A missing stage stays missing.
 */

export const STAGE_UNAVAILABLE = 'Stage attribution unavailable';
export const NOT_AVAILABLE = 'Not available';

const TYPE_NAMES: Record<number, [singular: string, plural: string]> = {
  0: ['Functional Requirement', 'Functional Requirements'],
  1: ['Non-Functional Requirement', 'Non-Functional Requirements'],
  2: ['Business Rule', 'Business Rules'],
  3: ['User Story', 'User Stories'],
  4: ['Acceptance Criterion', 'Acceptance Criteria'],
  5: ['Clarification Question', 'Clarification Questions'],
  6: ['Solution Design', 'Solution Designs'],
  7: ['API Specification', 'API Specifications'],
  8: ['Data Entity', 'Data Entities'],
  9: ['Implementation Task', 'Implementation Tasks'],
  10: ['Test Case', 'Test Cases'],
  11: ['Business Objective', 'Business Objectives'],
};

export function artifactTypeName(type: number, plural = false): string {
  const names = TYPE_NAMES[type];
  return names ? names[plural ? 1 : 0] : `Type ${type}`;
}

/** "RequirementGeneration" -> "Requirement Generation". */
export function humanize(pascal: string): string {
  return pascal.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/([A-Z])([A-Z][a-z])/g, '$1 $2');
}

export function stageOrUnavailable(stage: number | null | undefined): string {
  return stage == null ? STAGE_UNAVAILABLE : stageLabel(stage);
}

export const WORK_OUTCOME_LABEL: Record<number, string> = {
  [WORK_OUTCOME.TaskCompleted]: 'Task completed',
  [WORK_OUTCOME.TaskReopened]: 'Task reopened',
  [WORK_OUTCOME.TestPassed]: 'Test passed',
  [WORK_OUTCOME.TestFailed]: 'Test failed',
  [WORK_OUTCOME.TestBlocked]: 'Test blocked',
};

/** Date and time, e.g. "Sep 25, 2026, 22:06". */
export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return NOT_AVAILABLE;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return NOT_AVAILABLE;
  return d.toLocaleString(undefined, { month: 'short', day: 'numeric', year: 'numeric', hour: '2-digit', minute: '2-digit' });
}

/** "2h 13m", "3d 4h": how long a stage visit lasted. */
export function formatDuration(fromIso: string, toIso: string): string {
  const minutes = Math.max(0, Math.round((new Date(toIso).getTime() - new Date(fromIso).getTime()) / 60_000));
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ${minutes % 60}m`;
  return `${Math.floor(hours / 24)}d ${hours % 24}h`;
}

/** The artifacts first created while the project was in this stage, in creation order. */
export function artifactsCreatedIn(review: LifecycleReview, stage: number): ReviewedArtifact[] {
  return review.artifacts.filter((a) => a.createdInStage === stage);
}

export function artifactsById(review: LifecycleReview): Map<string, ReviewedArtifact> {
  return new Map(review.artifacts.map((a) => [a.id, a]));
}

/** Timeline events grouped by local calendar day, oldest first (the order the backend sends). */
export function groupByDay(events: ReviewTimelineEvent[]): { day: string; events: ReviewTimelineEvent[] }[] {
  const groups: { day: string; events: ReviewTimelineEvent[] }[] = [];
  for (const e of events) {
    const day = new Date(e.at).toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric', year: 'numeric' });
    const last = groups[groups.length - 1];
    if (last && last.day === day) last.events.push(e);
    else groups.push({ day, events: [e] });
  }
  return groups;
}

export type TimelineFilter = 'all' | 'lifecycle' | 'ai' | 'reviews' | 'work';

const FILTER_KINDS: Record<Exclude<TimelineFilter, 'all'>, number[]> = {
  lifecycle: [TIMELINE_EVENT_KIND.ProjectCreated, TIMELINE_EVENT_KIND.StageTransition, TIMELINE_EVENT_KIND.RequirementSourceAdded],
  ai: [TIMELINE_EVENT_KIND.AiGeneration, TIMELINE_EVENT_KIND.AgentStep],
  reviews: [TIMELINE_EVENT_KIND.ArtifactsReviewed, TIMELINE_EVENT_KIND.ArtifactsEdited, TIMELINE_EVENT_KIND.ArtifactsCreated],
  work: [TIMELINE_EVENT_KIND.WorkRecorded],
};

export function filterTimeline(events: ReviewTimelineEvent[], filter: TimelineFilter, stage: number | 'all' | 'none' = 'all'): ReviewTimelineEvent[] {
  return events.filter(
    (e) =>
      (filter === 'all' || FILTER_KINDS[filter].includes(e.kind)) &&
      (stage === 'all' || (stage === 'none' ? e.stage == null : e.stage === stage)),
  );
}

/**
 * Where a completed delivery's artifacts sit along requirement → story → criteria → design → task →
 * test, using the backend's per-type totals. Types with none are kept, so a gap shows as zero.
 */
export const CHAIN_TYPES: number[] = [0, 3, 4, 6, 9, 10];
