import { apiClient } from './client';
import type { CopilotAnswer, CopilotLimits } from './types';

export const copilotApi = {
  ask: (projectId: string, question: string, token: string | null) =>
    apiClient.post<CopilotAnswer>(`/api/projects/${projectId}/copilot/ask`, { question }, token),
  limits: (projectId: string, token: string | null) =>
    apiClient.get<CopilotLimits>(`/api/projects/${projectId}/copilot/limits`, token),
};
