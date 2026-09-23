import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useAuth } from '../auth/AuthContext';
import { apiClient, ApiError } from '../api/client';
import type { Project } from '../api/types';

const LAST_PROJECT_KEY = 'aireap.last-project';

export function readLastProject(): { id: string; name: string } | null {
  try {
    const raw = localStorage.getItem(LAST_PROJECT_KEY);
    return raw ? (JSON.parse(raw) as { id: string; name: string }) : null;
  } catch {
    return null;
  }
}

interface ProjectContextValue {
  /** Id taken from the URL, or null when outside a project. */
  projectId: string | null;
  project: Project | null;
  loading: boolean;
  error: string | null;
  reload: () => void;
  setProject: (project: Project) => void;
  /** Bumped whenever project content (requirements, artifacts) changes, so panels can refresh. */
  contentVersion: number;
  bumpContent: () => void;
}

const ProjectContext = createContext<ProjectContextValue | undefined>(undefined);

/** Loads the project addressed by the URL once, and shares it with the shell and every phase page. */
export function ProjectProvider({ projectId, children }: { projectId: string | null; children: ReactNode }) {
  const { token } = useAuth();
  const [project, setProject] = useState<Project | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [contentVersion, setContentVersion] = useState(0);

  useEffect(() => {
    if (!projectId) {
      setProject(null);
      setError(null);
      setLoading(false);
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    apiClient
      .get<Project>(`/api/projects/${projectId}`, token)
      .then((p) => {
        if (cancelled) return;
        setProject(p);
        try {
          localStorage.setItem(LAST_PROJECT_KEY, JSON.stringify({ id: p.id, name: p.name }));
        } catch {
          // best effort
        }
      })
      .catch((err) => {
        if (cancelled) return;
        setProject(null);
        setError(err instanceof ApiError && err.status === 404 ? 'not-found' : 'failed');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [projectId, token, attempt]);

  const reload = useCallback(() => setAttempt((a) => a + 1), []);
  const bumpContent = useCallback(() => setContentVersion((v) => v + 1), []);

  const value = useMemo<ProjectContextValue>(
    () => ({
      projectId,
      // Never hand out a project that does not match the URL (avoids a flash of the previous project).
      project: project && project.id === projectId ? project : null,
      loading,
      error,
      reload,
      setProject,
      contentVersion,
      bumpContent,
    }),
    [projectId, project, loading, error, reload, contentVersion, bumpContent],
  );

  return <ProjectContext.Provider value={value}>{children}</ProjectContext.Provider>;
}

export function useProjectContext(): ProjectContextValue {
  const ctx = useContext(ProjectContext);
  if (!ctx) throw new Error('useProjectContext must be used within a ProjectProvider');
  return ctx;
}

/** For pages rendered under ProjectGate, where the project is guaranteed to be loaded. */
export function useProject() {
  const ctx = useProjectContext();
  if (!ctx.project) throw new Error('useProject called before the project was loaded');
  return { ...ctx, project: ctx.project };
}
