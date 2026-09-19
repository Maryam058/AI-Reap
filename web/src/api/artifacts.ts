import { apiClient } from './client';
import type {
  ArtifactRelationship,
  ArtifactSummary,
  ArtifactVersion,
  ClarificationAnswerRequest,
  UpdateArtifactStatusRequest,
} from './types';

export const artifactsApi = {
  listForProject: (projectId: string, token: string | null, filters?: { type?: number; requirementSourceId?: string }) => {
    const params = new URLSearchParams();
    if (filters?.type !== undefined) params.set('type', String(filters.type));
    if (filters?.requirementSourceId) params.set('requirementSourceId', filters.requirementSourceId);
    const query = params.toString();
    return apiClient.get<ArtifactSummary[]>(`/api/projects/${projectId}/artifacts${query ? `?${query}` : ''}`, token);
  },

  updateStatus: (artifactId: string, request: UpdateArtifactStatusRequest, token: string | null) =>
    apiClient.patch<ArtifactSummary>(`/api/artifacts/${artifactId}/status`, request, token),

  answerClarification: (artifactId: string, request: ClarificationAnswerRequest, token: string | null) =>
    apiClient.post<ArtifactSummary>(`/api/artifacts/${artifactId}/clarification-answer`, request, token),

  generateAcceptanceCriteria: (userStoryArtifactId: string, token: string | null) =>
    apiClient.post<ArtifactSummary[]>(`/api/artifacts/${userStoryArtifactId}/generate-acceptance-criteria`, {}, token),

  generateTestCases: (functionalRequirementArtifactId: string, token: string | null) =>
    apiClient.post<ArtifactSummary[]>(`/api/artifacts/${functionalRequirementArtifactId}/generate-test-cases`, {}, token),

  getRelationships: (artifactId: string, token: string | null) =>
    apiClient.get<ArtifactRelationship[]>(`/api/artifacts/${artifactId}/relationships`, token),

  getVersions: (artifactId: string, token: string | null) =>
    apiClient.get<ArtifactVersion[]>(`/api/artifacts/${artifactId}/versions`, token),
};
