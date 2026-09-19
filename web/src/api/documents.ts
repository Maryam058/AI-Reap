import { ApiError, apiClient } from './client';
import type { ProjectDocument } from './types';

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5299';

export const documentsApi = {
  listForProject: (projectId: string, token: string | null) =>
    apiClient.get<ProjectDocument[]>(`/api/projects/${projectId}/documents`, token),

  // TXT only for now (see ADR-001 §4) — multipart upload, so it bypasses the JSON apiClient.
  upload: async (projectId: string, file: File, token: string | null): Promise<ProjectDocument> => {
    const formData = new FormData();
    formData.append('file', file);

    const response = await fetch(`${API_BASE_URL}/api/projects/${projectId}/documents/upload`, {
      method: 'POST',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
      body: formData,
    });

    if (!response.ok) {
      const text = await response.text().catch(() => '');
      throw new ApiError(response.status, text || response.statusText);
    }

    return (await response.json()) as ProjectDocument;
  },
};
