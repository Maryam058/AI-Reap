import { apiClient } from './client';
import type { AiChunkRun, AiPlanOperation } from './types';

// Batched AI runs (AiRunsController). Every call answers 400 batch_mode_disabled while
// Ai:Groq:MaxBatchedRunParts is 0. Start sends nothing to an AI provider; Continue sends at most
// partsPerAction parts; Commit saves everything at once.
export const aiRunsApi = {
  start: (sourceId: string, operation: AiPlanOperation, token: string | null) =>
    apiClient.post<AiChunkRun>(`/api/requirement-sources/${sourceId}/ai-runs`, { operation }, token),

  // The active run, or undefined (204) when there is none.
  active: (sourceId: string, operation: AiPlanOperation, token: string | null) =>
    apiClient.get<AiChunkRun | undefined>(`/api/requirement-sources/${sourceId}/ai-runs?operation=${operation}`, token),

  get: (runId: string, token: string | null) => apiClient.get<AiChunkRun>(`/api/ai-runs/${runId}`, token),

  continue: (runId: string, token: string | null) => apiClient.post<AiChunkRun>(`/api/ai-runs/${runId}/continue`, {}, token),

  commit: (runId: string, token: string | null) => apiClient.post<AiChunkRun>(`/api/ai-runs/${runId}/commit`, {}, token),

  abandon: (runId: string, token: string | null) => apiClient.post<AiChunkRun>(`/api/ai-runs/${runId}/abandon`, {}, token),
};
