import { createContext, useContext } from 'react';
import type { ProjectLifecycleState } from '../api/types';

/**
 * The open project's lifecycle state, loaded once by the project workspace frame and shared with
 * the stage navigator and stage headers so they agree on each stage's status. Null until loaded
 * (or outside a project); stage statuses then fall back to Project.status alone.
 */
export const StageSignalsContext = createContext<ProjectLifecycleState | null>(null);

export function useStageSignals(): ProjectLifecycleState | null {
  return useContext(StageSignalsContext);
}
