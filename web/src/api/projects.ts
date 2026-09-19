import { apiClient } from './client';
import type {
  ConflictFinding,
  CreateStakeholderRequest,
  Project,
  Stakeholder,
  UpdateProjectRequest,
} from './types';

export const projectsApi = {
  update: (projectId: string, request: UpdateProjectRequest, token: string | null) =>
    apiClient.put<Project>(`/api/projects/${projectId}`, request, token),

  updateStatus: (projectId: string, status: number, token: string | null) =>
    apiClient.patch<Project>(`/api/projects/${projectId}/status`, { status }, token),

  detectConflicts: (projectId: string, token: string | null) =>
    apiClient.post<ConflictFinding[]>(`/api/projects/${projectId}/detect-conflicts`, {}, token),

  getStakeholders: (projectId: string, token: string | null) =>
    apiClient.get<Stakeholder[]>(`/api/projects/${projectId}/stakeholders`, token),

  addStakeholder: (projectId: string, request: CreateStakeholderRequest, token: string | null) =>
    apiClient.post<Stakeholder>(`/api/projects/${projectId}/stakeholders`, request, token),

  updateStakeholder: (projectId: string, stakeholderId: string, request: CreateStakeholderRequest, token: string | null) =>
    apiClient.put<Stakeholder>(`/api/projects/${projectId}/stakeholders/${stakeholderId}`, request, token),

  removeStakeholder: (projectId: string, stakeholderId: string, token: string | null) =>
    apiClient.delete(`/api/projects/${projectId}/stakeholders/${stakeholderId}`, token),
};
