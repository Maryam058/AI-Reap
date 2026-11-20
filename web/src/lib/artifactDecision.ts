// S1 — the body of an approve/reject request (PATCH /api/artifacts/{id}/status). It names the version
// the reviewer saw, so the API refuses the decision (409) if the artifact has changed since.
import type { UpdateArtifactStatusRequest } from '../api/types';

export function decisionRequest(status: number, comment: string | undefined, version: number): UpdateArtifactStatusRequest {
  return { status, comment, version };
}
