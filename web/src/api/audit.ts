import { apiClient } from './client';
import type { AiExecution } from './types';

export const auditApi = {
  getForProject: (projectId: string, token: string | null) =>
    apiClient.get<AiExecution[]>(`/api/projects/${projectId}/ai-executions`, token),
};
