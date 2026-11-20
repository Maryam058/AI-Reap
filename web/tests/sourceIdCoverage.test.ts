// Covers the P0-4 coverage notice RequirementWorkspace shows. The coverage itself is computed and
// tested on the server (SourceIdCoverageTests).
import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { SourceIdCoverage } from '../src/api/types.ts';
import { coverageNotice, listIds } from '../src/lib/sourceIdCoverage.ts';

const coverage = (over: Partial<SourceIdCoverage>): SourceIdCoverage => ({
  status: 'complete',
  definedCount: 4,
  covered: ['FR-001', 'FR-002', 'FR-003', 'NFR-001'],
  uncovered: [],
  coveredOnlyByRejected: [],
  mentionedOnly: [],
  ...over,
});

test('nothing to show until coverage is read', () => {
  assert.equal(coverageNotice(null), null);
  assert.equal(coverageNotice(undefined), null);
});

test('uncovered IDs are a warning naming them', () => {
  const notice = coverageNotice(coverage({ status: 'incomplete', covered: ['FR-001', 'FR-002', 'NFR-001'], uncovered: ['FR-003'] }))!;
  assert.equal(notice.tone, 'warning');
  assert.equal(notice.title, '1 of 4 requirement IDs in the source not covered by any requirement');
  assert.deepEqual(notice.details, ['Cited by no requirement: FR-003']);
});

test('IDs cited only by rejected requirements are named separately, not as never cited', () => {
  const notice = coverageNotice(
    coverage({ status: 'incomplete', covered: ['FR-001', 'FR-002'], uncovered: ['FR-003', 'NFR-001'], coveredOnlyByRejected: ['nfr-001'] }),
  )!;
  assert.equal(notice.tone, 'warning');
  assert.match(notice.title, /^2 of 4 /);
  assert.deepEqual(notice.details, ['Cited by no requirement: FR-003', 'Cited only by rejected requirements: nfr-001']);

  const onlyRejected = coverageNotice(coverage({ status: 'incomplete', uncovered: ['NFR-001'], coveredOnlyByRejected: ['NFR-001'] }))!;
  assert.deepEqual(onlyRejected.details, ['Cited only by rejected requirements: NFR-001']);
});

test('mentioned-only identifiers are listed in every state, never hidden', () => {
  const mentioned = ['SHA-256', 'ISO-27001'];
  for (const status of ['complete', 'incomplete', 'not_applicable'] as const) {
    const notice = coverageNotice(coverage({ status, mentionedOnly: mentioned, uncovered: status === 'incomplete' ? ['FR-003'] : [] }))!;
    assert.ok(
      notice.details.includes('Mentioned in the text but not written as requirements, so not checked: SHA-256, ISO-27001'),
      status,
    );
  }
});

test('a source without defined IDs cannot be checked and is never reported complete', () => {
  const notice = coverageNotice(coverage({ status: 'not_applicable', definedCount: 0, covered: [] }))!;
  assert.equal(notice.tone, 'info');
  assert.equal(notice.title, 'Requirement ID coverage cannot be checked');
  assert.ok(!/every|cited \(/i.test(notice.title));
});

test('full coverage is a success line with the count', () => {
  assert.deepEqual(coverageNotice(coverage({})), { tone: 'success', title: 'Every requirement ID in the source is cited (4 IDs)', details: [] });
  assert.equal(coverageNotice(coverage({ definedCount: 1, covered: ['FR-001'] }))!.title, 'Every requirement ID in the source is cited (1 ID)');
});

test('long ID lists are shortened with a count', () => {
  const ids = Array.from({ length: 15 }, (_, i) => `FR-${String(i + 1).padStart(3, '0')}`);
  assert.equal(listIds(ids), `${ids.slice(0, 12).join(', ')} and 3 more`);
  assert.equal(listIds(ids.slice(0, 2)), 'FR-001, FR-002');
});
