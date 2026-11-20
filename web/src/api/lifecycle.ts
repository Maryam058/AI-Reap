import { apiClient } from './client';
import type {
  AdvanceStageRequest,
  DecideStageTransitionRequest,
  LifecycleStagePolicy,
  ProjectLifecycleState,
  StageReview,
  StageTransition,
} from './types';

// §5 — the project lifecycle (LifecycleController). The backend enforces stage ownership, gates,
// evidence and separation of duties; these calls only ask.
export const lifecycleApi = {
  get: (projectId: string, token: string | null) =>
    apiClient.get<ProjectLifecycleState>(`/api/projects/${projectId}/lifecycle`, token),

  history: (projectId: string, token: string | null) =>
    apiClient.get<StageTransition[]>(`/api/projects/${projectId}/lifecycle/history`, token),

  policy: (token: string | null) => apiClient.get<LifecycleStagePolicy[]>('/api/lifecycle/policy', token),

  /** Direct stages move at once; Design and Testing create a pending request instead. */
  advance: (projectId: string, request: AdvanceStageRequest, token: string | null) =>
    apiClient.post<StageTransition>(`/api/projects/${projectId}/lifecycle/transitions`, request, token),

  decide: (transitionId: string, request: DecideStageTransitionRequest, token: string | null) =>
    apiClient.post<StageTransition>(`/api/stage-transitions/${transitionId}/decision`, request, token),

  withdraw: (transitionId: string, note: string | undefined, token: string | null) =>
    apiClient.post<StageTransition>(`/api/stage-transitions/${transitionId}/withdraw`, { note }, token),

  comment: (transitionId: string, comment: string, token: string | null) =>
    apiClient.post<StageReview>(`/api/stage-transitions/${transitionId}/comments`, { comment }, token),

  rollback: (projectId: string, targetStatus: number, reason: string, token: string | null) =>
    apiClient.post<StageTransition>(`/api/projects/${projectId}/lifecycle/rollback`, { targetStatus, reason }, token),
};
