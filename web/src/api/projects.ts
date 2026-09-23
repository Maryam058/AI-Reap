import { apiClient } from './client';
import type {
  AddProjectMemberRequest,
  ConflictFinding,
  CreateProjectRequest,
  CreateStakeholderRequest,
  Project,
  ProjectMember,
  Stakeholder,
  UpdateProjectRequest,
} from './types';

export const projectsApi = {
  list: (token: string | null) => apiClient.get<Project[]>('/api/projects', token),

  create: (request: CreateProjectRequest, token: string | null) =>
    apiClient.post<Project>('/api/projects', request, token),

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

  getMembers: (projectId: string, token: string | null) =>
    apiClient.get<ProjectMember[]>(`/api/projects/${projectId}/members`, token),

  addMember: (projectId: string, request: AddProjectMemberRequest, token: string | null) =>
    apiClient.post<ProjectMember>(`/api/projects/${projectId}/members`, request, token),

  removeMember: (projectId: string, userId: string, token: string | null) =>
    apiClient.delete(`/api/projects/${projectId}/members/${userId}`, token),
};
