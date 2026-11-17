import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useAuth } from '../auth/AuthContext';
import { apiClient, ApiError } from '../api/client';
import { lifecycleApi } from '../api/lifecycle';
import type { Project, ProjectLifecycleState } from '../api/types';

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
  /**
   * The open project's lifecycle state (exit checks, evidence gaps, pending request), loaded once
   * here for every consumer - stage navigator, sidebar, stage headers, gap panels and the lifecycle
   * panel - so they always agree. Re-read whenever the project or its content changes, so a
   * resolved gap disappears everywhere at once. Null until loaded.
   */
  lifecycle: ProjectLifecycleState | null;
  lifecycleFailed: boolean;
  reloadLifecycle: () => void;
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

  // One lifecycle fetch for the open project. Keyed on the project's status/updatedAt (a stage move,
  // a context edit) and on contentVersion (an artifact approved, generated, edited, a result recorded).
  const [lifecycle, setLifecycle] = useState<ProjectLifecycleState | null>(null);
  const [lifecycleFailed, setLifecycleFailed] = useState(false);
  const [lifecycleAttempt, setLifecycleAttempt] = useState(0);
  const loadedId = project && project.id === projectId ? project.id : null;
  const lifecycleKey = project && loadedId ? `${project.status}|${project.updatedAt}` : '';
  useEffect(() => {
    if (!loadedId) {
      setLifecycle(null);
      return;
    }
    let cancelled = false;
    lifecycleApi
      .get(loadedId, token)
      .then((s) => {
        if (cancelled) return;
        setLifecycle(s);
        setLifecycleFailed(false);
      })
      .catch(() => !cancelled && setLifecycleFailed(true));
    return () => {
      cancelled = true;
    };
  }, [loadedId, lifecycleKey, contentVersion, lifecycleAttempt, token]);
  const reloadLifecycle = useCallback(() => setLifecycleAttempt((a) => a + 1), []);
  const lifecycleForProject = lifecycle && lifecycle.projectId === loadedId ? lifecycle : null;

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
      lifecycle: lifecycleForProject,
      lifecycleFailed,
      reloadLifecycle,
    }),
    [projectId, project, loading, error, reload, contentVersion, bumpContent, lifecycleForProject, lifecycleFailed, reloadLifecycle],
  );

  return <ProjectContext.Provider value={value}>{children}</ProjectContext.Provider>;
}

/** The project context when inside a ProjectProvider, otherwise undefined (never throws). */
export function useOptionalProjectContext(): ProjectContextValue | undefined {
  return useContext(ProjectContext);
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
