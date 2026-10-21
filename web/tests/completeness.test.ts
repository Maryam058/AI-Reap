// Covers the P0-7 completeness notice RequirementWorkspace shows after Analyze/Generate. The flags
// themselves are computed and tested on the server (CompletenessFlagsTests).
import assert from 'node:assert/strict';
import { test } from 'node:test';
import type {
  AnalyzeRequirementResult,
  ArtifactSummary,
  GenerateRequirementsResult,
  RequirementCompleteness,
  SourceIdCoverage,
} from '../src/api/types.ts';
import { analysisCompletenessNotice, generationCompletenessNotice } from '../src/lib/completeness.ts';

const item = { id: 'a' } as unknown as ArtifactSummary;

const completeCoverage: SourceIdCoverage = {
  status: 'complete',
  definedCount: 2,
  covered: ['FR-001', 'NFR-001'],
  uncovered: [],
  coveredOnlyByRejected: [],
  mentionedOnly: [],
};

const flags = (over: Partial<RequirementCompleteness> = {}): RequirementCompleteness => ({
  contextReduction: 'none',
  partCount: 1,
  clarifications: 'whole',
  models: ['gemini:gemini-3-flash'],
  unverifiedEvidenceCount: 0,
  noEvidenceCount: 0,
  unsupportedNumbersCount: 0,
  ...over,
});

const generation = (over: Partial<GenerateRequirementsResult> = {}): GenerateRequirementsResult => ({
  functionalRequirements: [item],
  nonFunctionalRequirements: [item],
  skippedDuplicates: 0,
  sourceIdCoverage: completeCoverage,
  completeness: flags(),
  ...over,
});

const analysis = (completeness?: RequirementCompleteness | null): AnalyzeRequirementResult => ({
  actors: [],
  capabilities: [],
  dataElements: [],
  notes: [],
  clarificationQuestions: [],
  completeness,
});

test('an older API without completeness shows nothing', () => {
  assert.equal(generationCompletenessNotice(null), null);
  assert.equal(generationCompletenessNotice(generation({ completeness: undefined })), null);
  assert.equal(generationCompletenessNotice(generation({ completeness: null })), null);
  assert.equal(analysisCompletenessNotice(analysis(undefined)), null);
  assert.equal(analysisCompletenessNotice(null), null);
});

test('success only when whole, coverage complete, every evidence count zero and something was created', () => {
  const notice = generationCompletenessNotice(generation())!;
  assert.equal(notice.tone, 'success');
  assert.equal(notice.title, 'Source processed whole, every requirement ID cited, and a quote found in SRS for every new requirement');
  assert.ok(notice.details.includes('Generated whole, in one request.'));
  assert.ok(notice.details.includes('Served by gemini:gemini-3-flash.'));

  for (const over of [
    { completeness: flags({ contextReduction: 'split', partCount: 3 }) },
    { completeness: flags({ unverifiedEvidenceCount: 1 }) },
    { completeness: flags({ noEvidenceCount: 1 }) },
    { completeness: flags({ unsupportedNumbersCount: 1 }) },
    { completeness: flags({ unverifiedEvidenceCount: null }) }, // counts not reported
    { sourceIdCoverage: { ...completeCoverage, status: 'incomplete' as const, uncovered: ['FR-002'] } },
    { sourceIdCoverage: { ...completeCoverage, status: 'not_applicable' as const, definedCount: 0, covered: [] } },
    { sourceIdCoverage: null },
    { sourceIdCoverage: undefined },
    { functionalRequirements: [], nonFunctionalRequirements: [], skippedDuplicates: 4 },
  ]) {
    assert.notEqual(generationCompletenessNotice(generation(over))!.tone, 'success', JSON.stringify(over));
  }
});

test('a split source is described as every passage sent once, not as missing content', () => {
  const notice = generationCompletenessNotice(generation({ completeness: flags({ contextReduction: 'split', partCount: 3 }) }))!;
  assert.equal(notice.tone, 'info');
  assert.equal(notice.title, 'Result generated with limitations');
  assert.ok(
    notice.details.includes(
      'Generated in 3 parts: every passage of the source was sent once, but the AI could not relate statements in different parts.',
    ),
  );
  assert.ok(notice.details.includes('Every part carried all clarifications.'));
  assert.ok(!notice.details.some((l) => /missing|omitted|dropped|incomplete/i.test(l)));
});

test('clarifications are whole, distributed or absent', () => {
  const lines = (c: string, split = true) =>
    generationCompletenessNotice(generation({ completeness: flags({ clarifications: c, contextReduction: split ? 'split' : 'none', partCount: split ? 2 : 1 }) }))!.details;
  assert.ok(lines('distributed').includes('Each part carried the clarifications most relevant to it; every clarification went with at least one part.'));
  assert.ok(lines('whole', false).includes('All clarifications were sent with the source.'));
  assert.ok(!lines('none').some((l) => /clarification/i.test(l)));
});

test('evidence warnings name each count over the new requirements', () => {
  const notice = generationCompletenessNotice(
    generation({ completeness: flags({ unverifiedEvidenceCount: 2, noEvidenceCount: 1, unsupportedNumbersCount: 1 }) }),
  )!;
  assert.equal(notice.tone, 'warning');
  assert.equal(notice.title, 'Check this result before relying on it');
  assert.ok(notice.details.includes('2 new requirements have a quote that was not found as written in the SRS.'));
  assert.ok(notice.details.includes('1 new requirement quotes no evidence.'));
  assert.ok(notice.details.includes('1 new requirement states a number that none of its quotes contains.'));
});

test('coverage comes from sourceIdCoverage through the P0-4 notice and is never shown as complete when missing', () => {
  const partial = generationCompletenessNotice(
    generation({ sourceIdCoverage: { ...completeCoverage, status: 'incomplete', covered: ['FR-001'], uncovered: ['NFR-001'] } }),
  )!;
  assert.equal(partial.tone, 'warning');
  assert.ok(partial.details.includes('1 of 2 requirement IDs in the source not covered by any requirement.'));

  const rejectedOnly = generationCompletenessNotice(
    generation({
      sourceIdCoverage: { ...completeCoverage, status: 'incomplete', covered: ['FR-001'], uncovered: ['NFR-001'], coveredOnlyByRejected: ['NFR-001'] },
    }),
  )!;
  assert.equal(rejectedOnly.tone, 'warning');

  const notApplicable = generationCompletenessNotice(
    generation({ sourceIdCoverage: { ...completeCoverage, status: 'not_applicable', definedCount: 0, covered: [] } }),
  )!;
  assert.equal(notApplicable.tone, 'info');
  assert.ok(notApplicable.details.includes('Requirement ID coverage cannot be checked.'));

  const missing = generationCompletenessNotice(generation({ sourceIdCoverage: null }))!;
  assert.equal(missing.tone, 'info');
  assert.ok(missing.details.includes('Requirement ID coverage was not reported.'));
});

test('a re-run that only found duplicates says nothing new was saved', () => {
  const notice = generationCompletenessNotice(generation({ functionalRequirements: [], nonFunctionalRequirements: [], skippedDuplicates: 4 }))!;
  assert.equal(notice.tone, 'info');
  assert.ok(notice.details.includes('No new requirements were saved; 4 requirements already existed.'));
});

test('models are listed as reported and nothing is named when none was reported', () => {
  const notice = generationCompletenessNotice(generation({ completeness: flags({ models: [] }) }))!;
  assert.ok(!notice.details.some((l) => l.startsWith('Served by')));
  const two = generationCompletenessNotice(generation({ completeness: flags({ models: ['gemini:x', 'groq:y'] }) }))!;
  assert.ok(two.details.includes('Served by gemini:x, groq:y.'));
});

test('analysis notice says whole or split and is never a success', () => {
  const whole = analysisCompletenessNotice(analysis(flags({ clarifications: 'none', unverifiedEvidenceCount: null })))!;
  assert.deepEqual(whole, { tone: 'info', title: 'Source analysed whole', details: ['Analysed whole, in one request.', 'Served by gemini:gemini-3-flash.'] });
  const split = analysisCompletenessNotice(analysis(flags({ contextReduction: 'split', partCount: 4, models: [] })))!;
  assert.equal(split.title, 'Source analysed in 4 parts');
  assert.equal(split.tone, 'info');
});

test('no wording calls a requirement verified', () => {
  const notices = [
    generationCompletenessNotice(generation()),
    generationCompletenessNotice(generation({ completeness: flags({ unverifiedEvidenceCount: 3, noEvidenceCount: 2, unsupportedNumbersCount: 1, contextReduction: 'split', partCount: 2, clarifications: 'distributed' }) })),
    analysisCompletenessNotice(analysis(flags())),
  ];
  for (const n of notices) {
    for (const text of [n!.title, ...n!.details]) assert.ok(!/verified requirement|\bverified\b/i.test(text), text);
  }
});
