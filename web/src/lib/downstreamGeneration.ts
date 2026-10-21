// A4a — whether ArtifactCard offers "Generate acceptance criteria" / "Generate test cases" for an
// artifact. A rejected artifact is not built on; this only hides the button, the API refuses the
// request itself (GenerationInputs in AiReap.Application, HTTP 409).
import { ARTIFACT_STATUS } from '../api/types.ts';

export function canGenerateDownstream(status: number): boolean {
  return status !== ARTIFACT_STATUS.Rejected;
}
