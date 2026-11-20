import { apiClient } from './client';
import type { SystemSettings } from './types';

// Administrator-only; the backend enforces this (403 for everyone else) regardless of the UI.
export const settingsApi = {
  get: (token: string | null) => apiClient.get<SystemSettings>('/api/settings', token),
};
