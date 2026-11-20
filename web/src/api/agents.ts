import { apiClient } from './client';
import type { AgentDefinition, AgentRun, AgentRunSummary } from './types';

export const agentsApi = {
  definitions: (token: string | null) => apiClient.get<AgentDefinition[]>('/api/agents', token),

  listForProject: (projectId: string, token: string | null) =>
    apiClient.get<AgentRun[]>(`/api/projects/${projectId}/agent-runs`, token),

  /** Read-only AI activity overview, grouped by the lifecycle stage each agent serves. */
  summary: (projectId: string, token: string | null) =>
    apiClient.get<AgentRunSummary>(`/api/projects/${projectId}/agent-runs/summary`, token),

  start: (requirementSourceId: string, token: string | null) =>
    apiClient.post<AgentRun>('/api/agent-runs', { requirementSourceId }, token),

  decide: (runId: string, approve: boolean, comment: string | undefined, token: string | null) =>
    apiClient.post<AgentRun>(`/api/agent-runs/${runId}/decision`, { approve, comment }, token),

  retry: (runId: string, token: string | null) =>
    apiClient.post<AgentRun>(`/api/agent-runs/${runId}/retry`, {}, token),
};
