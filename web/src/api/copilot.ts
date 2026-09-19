import { apiClient } from './client';
import type { CopilotAnswer } from './types';

export const copilotApi = {
  ask: (projectId: string, question: string, token: string | null) =>
    apiClient.post<CopilotAnswer>(`/api/projects/${projectId}/copilot/ask`, { question }, token),
};
