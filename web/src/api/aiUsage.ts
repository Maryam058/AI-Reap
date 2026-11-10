import { apiClient } from './client';
import type { AiUsageSnapshot } from './types';

// Administrator-only; the backend enforces this (403 for everyone else) regardless of the UI.
export const aiUsageApi = {
  get: (token: string | null) => apiClient.get<AiUsageSnapshot>('/api/ai/usage', token),
};
