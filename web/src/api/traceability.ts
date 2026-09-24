import { apiClient } from './client';
import type { BusinessObjective, ImpactAnalysisResult, ImpactNotice, TraceabilityRow, TraceRef } from './types';

export const traceabilityApi = {
  getMatrix: (projectId: string, token: string | null) =>
    apiClient.get<TraceabilityRow[]>(`/api/projects/${projectId}/traceability-matrix`, token),

  getImpact: (artifactId: string, token: string | null) =>
    apiClient.get<ImpactAnalysisResult>(`/api/artifacts/${artifactId}/impact`, token),

  getBusinessObjectives: (projectId: string, token: string | null) =>
    apiClient.get<BusinessObjective[]>(`/api/projects/${projectId}/business-objectives`, token),

  setBusinessObjectives: (requirementId: string, businessObjectiveIds: string[], token: string | null) =>
    apiClient.put<TraceRef[]>(`/api/artifacts/${requirementId}/business-objectives`, { businessObjectiveIds }, token),

  getProjectImpactNotices: (projectId: string, token: string | null, includeAcknowledged = false) =>
    apiClient.get<ImpactNotice[]>(`/api/projects/${projectId}/impact-notices?includeAcknowledged=${includeAcknowledged}`, token),

  getArtifactImpactNotices: (artifactId: string, token: string | null) =>
    apiClient.get<ImpactNotice[]>(`/api/artifacts/${artifactId}/impact-notices`, token),

  acknowledgeImpactNotice: (noticeId: string, note: string | undefined, token: string | null) =>
    apiClient.post<ImpactNotice>(`/api/impact-notices/${noticeId}/acknowledge`, { note }, token),
};
