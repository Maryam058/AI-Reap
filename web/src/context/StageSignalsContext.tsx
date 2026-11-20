import type { ProjectLifecycleState } from '../api/types';
import { useOptionalProjectContext } from './ProjectContext';

/**
 * The open project's lifecycle state, loaded once by ProjectProvider and shared with the stage
 * navigator, sidebar, stage headers and gap panels so they agree on each stage's status. Null until
 * loaded (or outside a project); stage statuses then fall back to Project.status alone.
 */
export function useStageSignals(): ProjectLifecycleState | null {
  return useOptionalProjectContext()?.lifecycle ?? null;
}
