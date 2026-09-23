import { apiClient } from './client';
import type { CapabilityGroup } from './types';

export const rolesApi = {
  getMatrix: (token: string | null) => apiClient.get<CapabilityGroup[]>('/api/roles/matrix', token),
};
