import { apiClient } from './client';
import type { ArtifactWorkRecord, WorkOutcome } from './types';

// Implementation / verification evidence (ArtifactWorkService): a Developer completes an approved
// task, QA records an approved test case's outcome. Recorded beside the artifact - its content,
// version and approval never change. The backend decides who may record what.
export const workRecordsApi = {
  list: (artifactId: string, token: string | null) =>
    apiClient.get<ArtifactWorkRecord[]>(`/api/artifacts/${artifactId}/work-records`, token),

  record: (artifactId: string, outcome: WorkOutcome, note: string | undefined, token: string | null) =>
    apiClient.post<ArtifactWorkRecord>(`/api/artifacts/${artifactId}/work-records`, { outcome, note }, token),
};
