// Covers the lifecycle review's display helpers. Stage attribution itself is decided and tested in
// the backend (LifecycleReviewTests); these only check nothing is inferred on the way to the screen.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { LifecycleReview, ReviewedArtifact, ReviewTimelineEvent } from '../src/api/types.ts';
import {
  artifactsCreatedIn,
  artifactTypeName,
  filterTimeline,
  formatDuration,
  groupByDay,
  humanize,
  STAGE_UNAVAILABLE,
  stageOrUnavailable,
} from '../src/lib/lifecycleReview.ts';

function artifact(id: string, createdInStage: number | null): ReviewedArtifact {
  return {
    id, code: id, title: id, artifactType: 0, status: 3, origin: 0, currentVersion: 1, versionCount: 1,
    createdByUserId: 'u', createdAt: '2026-01-01T09:00:00Z', updatedAt: '2026-01-01T09:00:00Z',
    createdInStage, servesStage: 2, reviewCount: 0, links: [],
  };
}

function event(at: string, kind: number, stage: number | null): ReviewTimelineEvent {
  return { at, kind, title: 't', stage, count: 1, artifactIds: [] };
}

test('a missing stage is reported as unavailable, never as a guess', () => {
  assert.equal(stageOrUnavailable(null), STAGE_UNAVAILABLE);
  assert.equal(stageOrUnavailable(undefined), STAGE_UNAVAILABLE);
  assert.equal(stageOrUnavailable(1), 'Requirements Gathering');
});

test('artifacts are listed under the stage the backend attributed, unattributed ones under none', () => {
  const review = { artifacts: [artifact('a', 1), artifact('b', null), artifact('c', 2), artifact('d', 1)] } as LifecycleReview;
  assert.deepEqual(artifactsCreatedIn(review, 1).map((a) => a.id), ['a', 'd']);
  assert.deepEqual(artifactsCreatedIn(review, 2).map((a) => a.id), ['c']);
  assert.deepEqual(artifactsCreatedIn(review, 7), []);
});

test('timeline filters by kind and by stage, including events with no stage', () => {
  const events = [event('2026-01-01T08:00:00Z', 1, 0), event('2026-01-01T09:00:00Z', 3, 1), event('2026-01-01T07:00:00Z', 3, null), event('2026-01-01T13:00:00Z', 8, 5)];
  assert.equal(filterTimeline(events, 'ai').length, 2);
  assert.equal(filterTimeline(events, 'ai', 1).length, 1);
  assert.equal(filterTimeline(events, 'all', 'none').length, 1);
  assert.equal(filterTimeline(events, 'work').length, 1);
  assert.equal(filterTimeline(events, 'lifecycle').length, 1);
});

test('timeline groups keep the backend order and split on calendar days', () => {
  const groups = groupByDay([event('2026-01-01T10:00:00', 1, 0), event('2026-01-01T11:00:00', 1, 0), event('2026-01-03T10:00:00', 1, 1)]);
  assert.equal(groups.length, 2);
  assert.equal(groups[0].events.length, 2);
});

test('labels and durations', () => {
  assert.equal(artifactTypeName(4, true), 'Acceptance Criteria');
  assert.equal(artifactTypeName(1), 'Non-Functional Requirement');
  assert.equal(humanize('RequirementGeneration'), 'Requirement Generation');
  assert.equal(humanize('QA'), 'QA');
  assert.equal(formatDuration('2026-01-01T10:00:00Z', '2026-01-01T12:13:00Z'), '2h 13m');
  assert.equal(formatDuration('2026-01-01T10:00:00Z', '2026-01-04T14:00:00Z'), '3d 4h');
});
