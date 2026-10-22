import { ApiError, apiClient } from './client';
import type {
  AiPlanOperation,
  AiPlanResult,
  AnalyzeRequirementResult,
  ArtifactSummary,
  CreateRequirementSourceRequest,
  GenerateRequirementsResult,
  QualityFinding,
  RequirementSource,
  SourceIdCoverage,
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

  // Dry run: how each configured AI provider would take this source. Contacts no AI provider.
  aiPlan: (sourceId: string, operation: AiPlanOperation, token: string | null) =>
    apiClient.get<AiPlanResult>(`/api/requirement-sources/${sourceId}/ai-plan?operation=${operation}`, token),

  // P0-4 — which requirement IDs in the source no saved, non-rejected FR/NFR cites. Read-only; refused
  // (403) to a user who may not read raw sources.
  sourceIdCoverage: (sourceId: string, token: string | null) =>
    apiClient.get<SourceIdCoverage>(`/api/requirement-sources/${sourceId}/source-id-coverage`, token),

  // The retained source text. Refused (403) to a user who may not read raw sources or is not a
  // member of the source's project.
  getById: (sourceId: string, token: string | null) =>
    apiClient.get<RequirementSource>(`/api/requirement-sources/${encodeURIComponent(sourceId)}`, token),

  // The kept original PDF/DOCX, as bytes, for the in-app viewer: fetched with the token, never through a
  // link, so it is never a URL a user could share or the browser could cache. 404 for a source with no
  // kept original (or in another project), 403 for a role that may not read raw sources.
  getOriginal: async (sourceId: string, token: string | null, signal?: AbortSignal): Promise<ArrayBuffer> => {
    const response = await fetch(`${API_BASE_URL}/api/requirement-sources/${encodeURIComponent(sourceId)}/original`, {
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
      cache: 'no-store',
      signal,
    });

    if (!response.ok) {
      const text = await response.text().catch(() => '');
      throw new ApiError(response.status, text || response.statusText);
    }

    return response.arrayBuffer();
  },

  // Keeps the PDF/DOCX of a source uploaded before originals were kept, so the viewer can show it.
  // 422 when the file's text is not the source's text (another document or version), 409 when the
  // source already has its original or was not uploaded as that type.
  attachOriginal: async (sourceId: string, file: File, token: string | null): Promise<RequirementSource> => {
    const formData = new FormData();
    formData.append('file', file);

    const response = await fetch(`${API_BASE_URL}/api/requirement-sources/${encodeURIComponent(sourceId)}/original`, {
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
