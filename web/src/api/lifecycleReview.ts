import { apiClient } from './client';
import type { LifecycleReview } from './types';

// Read-only stage-by-stage review of a project's lifecycle (LifecycleReviewController).
export const lifecycleReviewApi = {
  get: (projectId: string, token: string | null) => apiClient.get<LifecycleReview>(`/api/projects/${projectId}/lifecycle-review`, token),
};
