// Covers when ArtifactCard / the test-case list offer AI generation for an artifact (A4a). Visibility
// only: the API refuses generation for a rejected artifact itself (AiReap.Tests GenerationInputIntegrityTests).
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ARTIFACT_STATUS } from '../src/api/types.ts';
import { canGenerateDownstream } from '../src/lib/downstreamGeneration.ts';

test('a rejected artifact offers no generation; every other status does, as before', () => {
  for (const [name, status] of Object.entries(ARTIFACT_STATUS)) {
    assert.equal(canGenerateDownstream(status), name !== 'Rejected', name);
  }
});
