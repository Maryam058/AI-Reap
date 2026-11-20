// Covers the P0-2 evidence labels ArtifactCard shows. The server's checks themselves are tested in
// AiReap.Tests (RequirementEvidenceTests).
import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { NonFunctionalRequirementData, RequirementEvidenceData } from '../src/api/types.ts';
import { evidenceSummary, originLabel, quoteLabel, targetLabel, unsupportedNumbersLabel } from '../src/lib/evidence.ts';

const CHECKED = 'SourceEvidence-v1';

test('each origin has its own label; an unrecognised one is shown as written, never reclassified', () => {
  assert.equal(originLabel('explicit')?.text, 'Explicit in source');
  assert.equal(originLabel('Derived ')?.text, 'Derived from source');
  assert.equal(originLabel('assumed')?.tone, 'warning');
  assert.equal(originLabel('stated-in-meeting')?.text, 'Origin (unrecognised): stated-in-meeting');
  assert.equal(originLabel(null)?.text, 'Origin not stated');
  assert.equal(originLabel(undefined), null); // a record made before origins existed
});

test('the evidence summary follows the stored status, and legacy records say so', () => {
  assert.equal(evidenceSummary({}).text, 'No evidence recorded (generated before evidence checks)');
  assert.equal(evidenceSummary({ evidenceCheck: CHECKED, evidenceStatus: 'verified' }).tone, 'success');
  assert.equal(evidenceSummary({ evidenceCheck: CHECKED, evidenceStatus: 'unverified' }).text, 'A quote was not found as written');
  assert.equal(evidenceSummary({ evidenceCheck: CHECKED, evidenceStatus: 'none' }).text, 'No evidence quoted');
  // A status without a check record (e.g. a forged edit the server reverted) is not trusted here either.
  assert.equal(evidenceSummary({ evidenceStatus: 'verified' }).tone, 'neutral');
});

// S3 — stored data is not limited to the typed values: a status is normalised like the server's
// (trimmed, lower-cased) and anything else on a checked record is reported as unrecognised.
const summary = (evidenceCheck: string | null | undefined, evidenceStatus: string | null | undefined) =>
  evidenceSummary({ evidenceCheck, evidenceStatus } as RequirementEvidenceData);
const UNRECOGNISED = 'Evidence status not recognised (not verified)';
const LEGACY = 'No evidence recorded (generated before evidence checks)';

test('a checked record with a missing or unknown status is unrecognised, never verified or "no evidence"', () => {
  for (const status of [undefined, null, '', '   ', 'pending', 'verified-ish', 'quote_found']) {
    const label = summary(CHECKED, status);
    assert.deepEqual(label, { text: UNRECOGNISED, tone: 'warning' }, String(status));
  }
});

test('statuses differing only in case or surrounding whitespace keep their labels', () => {
  for (const status of ['Verified', 'VERIFIED', '  verified ', '\tVerified\n']) {
    assert.deepEqual(summary(CHECKED, status), { text: 'Every quote found as written', tone: 'success' }, JSON.stringify(status));
  }
  for (const status of ['Unverified', ' UNVERIFIED ']) {
    assert.deepEqual(summary(CHECKED, status), { text: 'A quote was not found as written', tone: 'warning' }, JSON.stringify(status));
  }
  for (const status of ['None', ' NONE ']) {
    assert.deepEqual(summary(CHECKED, status), { text: 'No evidence quoted', tone: 'warning' }, JSON.stringify(status));
  }
});

test('a missing or blank evidenceCheck is a record made before checks, unless its status is unverified', () => {
  for (const check of [undefined, null, '', '   ', '\t\n']) {
    for (const status of [undefined, null, '', 'verified', 'none', 'pending']) {
      assert.deepEqual(summary(check, status), { text: LEGACY, tone: 'neutral' }, `${JSON.stringify(check)} / ${status}`);
    }
  }
  assert.equal(summary(` ${CHECKED} `, 'verified').tone, 'success'); // a padded check record still counts
});

test('an unverified status is shown as unverified even with no evidence check, as the server gates it', () => {
  // RequirementApprovalEvidence blocks "unverified" before it looks at EvidenceCheck; the label must not
  // call such a record "generated before evidence checks".
  for (const check of [undefined, null, '', '   ', '\t\n', CHECKED]) {
    for (const status of ['unverified', 'Unverified', ' UNVERIFIED ', '\tunverified\n']) {
      assert.deepEqual(summary(check, status), { text: 'A quote was not found as written', tone: 'warning' }, `${JSON.stringify(check)} / ${JSON.stringify(status)}`);
    }
  }
  assert.deepEqual(evidenceSummary({ evidenceStatus: 'unverified' }), { text: 'A quote was not found as written', tone: 'warning' });
});

test('the recognised statuses keep their exact labels and tones', () => {
  assert.deepEqual(summary(CHECKED, 'verified'), { text: 'Every quote found as written', tone: 'success' });
  assert.deepEqual(summary(CHECKED, 'unverified'), { text: 'A quote was not found as written', tone: 'warning' });
  assert.deepEqual(summary(CHECKED, 'none'), { text: 'No evidence quoted', tone: 'warning' });
});

test('only an srs quote is labelled as found in the SRS', () => {
  assert.equal(quoteLabel({ from: 'srs', status: 'verified', sourceId: 'FR-001' }).text, 'Quote found in SRS (FR-001)');
  assert.equal(quoteLabel({ from: 'clarification', ref: 'CQ-003', status: 'verified' }).text, 'Quote found in clarification CQ-003');
  assert.equal(quoteLabel({ from: 'excerpt', ref: 'notes.pdf#3', status: 'verified' }).text, 'Quote found in notes.pdf#3');
  assert.equal(quoteLabel({ from: 'excerpt', status: 'verified', quoteWithheld: true }).text, 'Quote found in a project document');
  for (const from of ['clarification', 'excerpt', 'email', null]) {
    assert.ok(!quoteLabel({ from, status: 'verified', ref: 'CQ-1' }).text.includes('SRS'), String(from));
  }
});

test('an unverified quote says why, as a warning', () => {
  const altered = quoteLabel({ from: 'srs', status: 'unverified', reason: 'altered' });
  assert.equal(altered.tone, 'warning');
  assert.match(altered.text, /^Quote from SRS: not as written/);
  assert.match(quoteLabel({ from: 'clarification', ref: 'CQ-002', status: 'unverified', reason: 'clarification_unanswered' }).text, /not answered yet/);
  assert.match(quoteLabel({ status: 'unverified', reason: 'something_new' }).text, /not checked$/);
});

const nfr = (over: Partial<NonFunctionalRequirementData>): NonFunctionalRequirementData => ({
  category: 'Performance',
  targetValue: '3 seconds',
  assumptionStatus: 'proposed_assumption',
  ...over,
});

test('target labels: stated and found, claimed stated, AI-proposed - none of them confirms', () => {
  assert.equal(targetLabel(nfr({ targetOrigin: 'stated', targetSupport: 'quote_found' }))?.text, 'Stated in SRS — quote found — needs confirmation');
  assert.equal(
    targetLabel(nfr({ targetOrigin: 'stated', targetSupport: 'quote_found', assumptionStatus: 'confirmed' }))?.text,
    'Stated in SRS — quote found',
  );
  assert.equal(targetLabel(nfr({ targetOrigin: 'stated', targetSupport: 'not_supported' }))?.text, 'Claimed stated — not found in quoted SRS text');
  assert.equal(targetLabel(nfr({ targetOrigin: 'stated', targetSupport: null }))?.tone, 'warning');
  assert.equal(targetLabel(nfr({ targetOrigin: 'proposed' }))?.text, 'AI-proposed target');
  assert.equal(targetLabel(nfr({})), null); // legacy
  assert.equal(targetLabel(nfr({ targetValue: null, targetOrigin: 'stated' })), null);
});

test('unsupported numbers are a warning line, absent when empty', () => {
  assert.equal(unsupportedNumbersLabel(['30 minutes', '5']), 'Not in quoted evidence: 30 minutes, 5');
  assert.equal(unsupportedNumbersLabel([]), null);
  assert.equal(unsupportedNumbersLabel(undefined), null);
});
