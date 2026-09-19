import { apiClient } from './client';
import type { ProjectDashboard } from './types';

export const dashboardApi = {
  get: (projectId: string, token: string | null) =>
    apiClient.get<ProjectDashboard>(`/api/projects/${projectId}/dashboard`, token),
};
