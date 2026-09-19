import { ApiError, apiClient } from './client';
import type {
  AnalyzeRequirementResult,
  ArtifactSummary,
  CreateRequirementSourceRequest,
  GenerateRequirementsResult,
  QualityFinding,
  RequirementSource,
} from './types';

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5299';

export const requirementSourcesApi = {
  listForProject: (projectId: string, token: string | null) =>
    apiClient.get<RequirementSource[]>(`/api/projects/${projectId}/requirement-sources`, token),

  create: (projectId: string, request: CreateRequirementSourceRequest, token: string | null) =>
    apiClient.post<RequirementSource>(`/api/projects/${projectId}/requirement-sources`, request, token),

  // TXT only for now (see ADR-001 §4) — multipart upload, so it bypasses the JSON apiClient.
  upload: async (projectId: string, file: File, token: string | null): Promise<RequirementSource> => {
    const formData = new FormData();
    formData.append('file', file);

    const response = await fetch(`${API_BASE_URL}/api/projects/${projectId}/requirement-sources/upload`, {
      method: 'POST',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
      body: formData,
    });

    if (!response.ok) {
      const text = await response.text().catch(() => '');
      throw new ApiError(response.status, text || response.statusText);
    }

    return (await response.json()) as RequirementSource;
  },

  analyze: (sourceId: string, token: string | null) =>
    apiClient.post<AnalyzeRequirementResult>(`/api/requirement-sources/${sourceId}/analyze`, {}, token),

  getClarificationQuestions: (sourceId: string, token: string | null) =>
    apiClient.get<ArtifactSummary[]>(`/api/requirement-sources/${sourceId}/clarification-questions`, token),

  generateRequirements: (sourceId: string, token: string | null) =>
    apiClient.post<GenerateRequirementsResult>(`/api/requirement-sources/${sourceId}/generate-requirements`, {}, token),

  generateUserStories: (sourceId: string, token: string | null) =>
    apiClient.post<ArtifactSummary[]>(`/api/requirement-sources/${sourceId}/generate-user-stories`, {}, token),

  generateBusinessRules: (sourceId: string, token: string | null) =>
    apiClient.post<ArtifactSummary[]>(`/api/requirement-sources/${sourceId}/generate-business-rules`, {}, token),

  analyzeQuality: (sourceId: string, token: string | null) =>
    apiClient.post<QualityFinding[]>(`/api/requirement-sources/${sourceId}/analyze-quality`, {}, token),

  generateDesign: (sourceId: string, token: string | null) =>
    apiClient.post<ArtifactSummary>(`/api/requirement-sources/${sourceId}/generate-design`, {}, token),

  generateDataEntities: (sourceId: string, token: string | null) =>
    apiClient.post<ArtifactSummary[]>(`/api/requirement-sources/${sourceId}/generate-data-entities`, {}, token),

  generateApiSpecs: (sourceId: string, token: string | null) =>
    apiClient.post<ArtifactSummary[]>(`/api/requirement-sources/${sourceId}/generate-api-specs`, {}, token),

  generateTasks: (sourceId: string, token: string | null) =>
    apiClient.post<ArtifactSummary[]>(`/api/requirement-sources/${sourceId}/generate-tasks`, {}, token),
};
