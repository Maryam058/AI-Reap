import { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { artifactsApi } from '../api/artifacts';
import { dashboardApi } from '../api/dashboard';
import { projectsApi } from '../api/projects';
import { traceabilityApi } from '../api/traceability';
import type { ArtifactSummary, ProjectDashboard, Stakeholder, TraceabilityRow } from '../api/types';

export interface ProjectInsights {
  loading: boolean;
  /** Each part is null when its request failed, so the UI can say "unavailable" instead of showing zeros. */
  dashboard: ProjectDashboard | null;
  artifacts: ArtifactSummary[] | null;
  stakeholders: Stakeholder[] | null;
  matrix: TraceabilityRow[] | null;
  reload: () => void;
}

/** Loads the real, per-project numbers the project dashboard is built from. Re-runs when `version` changes. */
export function useProjectInsights(projectId: string, version = 0): ProjectInsights {
  const { token } = useAuth();
  const [state, setState] = useState<Omit<ProjectInsights, 'reload'>>({ loading: true, dashboard: null, artifacts: null, stakeholders: null, matrix: null });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setState((s) => ({ ...s, loading: true }));
    Promise.allSettled([
      dashboardApi.get(projectId, token),
      artifactsApi.listForProject(projectId, token),
      projectsApi.getStakeholders(projectId, token),
      traceabilityApi.getMatrix(projectId, token),
    ]).then(([d, a, s, m]) => {
      if (cancelled) return;
      setState({
        loading: false,
        dashboard: d.status === 'fulfilled' ? d.value : null,
        artifacts: a.status === 'fulfilled' ? a.value : null,
        stakeholders: s.status === 'fulfilled' ? s.value : null,
        matrix: m.status === 'fulfilled' ? m.value : null,
      });
    });
    return () => {
      cancelled = true;
    };
  }, [projectId, token, version, attempt]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);
  return { ...state, reload };
}
