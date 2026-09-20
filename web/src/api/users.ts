import { apiClient } from './client';
import type { CreateUserRequest, UserAccount } from './types';

// Administrator-only; the backend enforces this (403 for everyone else) regardless of the UI.
export const usersApi = {
  list: (token: string | null) => apiClient.get<UserAccount[]>('/api/users', token),
  create: (request: CreateUserRequest, token: string | null) => apiClient.post<UserAccount>('/api/users', request, token),
  setRole: (id: string, role: string, token: string | null) => apiClient.put<UserAccount>(`/api/users/${id}/role`, { role }, token),
  setActive: (id: string, isActive: boolean, token: string | null) =>
    apiClient.put<UserAccount>(`/api/users/${id}/status`, { isActive }, token),
};
