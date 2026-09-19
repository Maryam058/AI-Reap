import { apiClient } from './client';
import type { ImpactAnalysisResult, TraceabilityRow } from './types';

export const traceabilityApi = {
  getMatrix: (projectId: string, token: string | null) =>
    apiClient.get<TraceabilityRow[]>(`/api/projects/${projectId}/traceability-matrix`, token),

  getImpact: (artifactId: string, token: string | null) =>
    apiClient.get<ImpactAnalysisResult>(`/api/artifacts/${artifactId}/impact`, token),
};
