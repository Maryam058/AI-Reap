import { apiClient } from './client';
import type { ProjectNotification } from './types';

// In-app lifecycle notifications (handover, approval requested, rejected); each user sees only their own.
export const notificationsApi = {
  list: (includeAcknowledged: boolean, token: string | null) =>
    apiClient.get<ProjectNotification[]>(`/api/notifications?includeAcknowledged=${includeAcknowledged}`, token),

  acknowledge: (id: string, token: string | null) =>
    apiClient.post<ProjectNotification>(`/api/notifications/${id}/acknowledge`, {}, token),
};
