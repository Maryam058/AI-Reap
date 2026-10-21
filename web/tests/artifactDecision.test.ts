// S1 — an approve/reject request names the version the reviewer saw (the API refuses it with 409 once
// the artifact has changed). The request body is built by decisionRequest; there is no component test
// runner here, so the wiring through ArtifactCard and RequirementWorkspace is checked in their source.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { ARTIFACT_STATUS } from '../src/api/types.ts';
import { decisionRequest } from '../src/lib/artifactDecision.ts';

const source = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');

test('approve and reject requests carry the version decided on', () => {
  assert.deepEqual(decisionRequest(ARTIFACT_STATUS.Approved, undefined, 4), { status: ARTIFACT_STATUS.Approved, comment: undefined, version: 4 });
  assert.deepEqual(decisionRequest(ARTIFACT_STATUS.Rejected, 'Not in the SRS.', 2), { status: ARTIFACT_STATUS.Rejected, comment: 'Not in the SRS.', version: 2 });
  // Serialized, an absent comment is left out; the version never is.
  assert.equal(JSON.stringify(decisionRequest(ARTIFACT_STATUS.Approved, undefined, 1)), `{"status":${ARTIFACT_STATUS.Approved},"version":1}`);
});

test('the artifact card decides on the version it shows, for approve and reject alike', () => {
  const card = source('../src/components/ArtifactCard.tsx');
  assert.match(card, /\(approve \? onApprove : onReject\)\(artifact\.id, trimmedComment \|\| undefined, artifact\.currentVersion\)/);
  assert.match(card, /onApprove: \(id: string, comment: string \| undefined, version: number\) => Promise<boolean>;/);
  assert.match(card, /onReject: \(id: string, comment: string \| undefined, version: number\) => Promise<boolean>;/);
});

test('the workspace sends that version with both decisions', () => {
  const workspace = source('../src/components/RequirementWorkspace.tsx');
  assert.match(workspace, /artifactsApi\.updateStatus\(id, decisionRequest\(status, comment, version\), token\)/);
  assert.match(workspace, /const onApprove = \(id: string, comment: string \| undefined, version: number\) => decideArtifact\(id, 3, comment, version\)/);
  assert.match(workspace, /const onReject = \(id: string, comment: string \| undefined, version: number\) => decideArtifact\(id, 4, comment, version\)/);
});
