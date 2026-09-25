import { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { lifecycleApi } from '../api/lifecycle';
import type { LifecycleStagePolicy, ProjectLifecycleState } from '../api/types';

/** A project's lifecycle state (stage, exit checks, pending request, allowed actions, history). */
export function useProjectLifecycle(projectId: string, version: unknown = 0) {
  const { token } = useAuth();
  const [state, setState] = useState<ProjectLifecycleState | null>(null);
  const [failed, setFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;
    lifecycleApi
      .get(projectId, token)
      .then((s) => {
        if (cancelled) return;
        setState(s);
        setFailed(false);
      })
      .catch(() => !cancelled && setFailed(true));
    return () => {
      cancelled = true;
    };
  }, [projectId, token, version, attempt]);

  const reload = useCallback(() => setAttempt((a) => a + 1), []);
  return { state: state?.projectId === projectId ? state : null, failed, reload };
}

let policyCache: Promise<LifecycleStagePolicy[]> | null = null;

/** Who owns each stage and which moves are gated, as the backend enforces it. Fetched once. */
export function useLifecyclePolicy(): LifecycleStagePolicy[] | null {
  const { token } = useAuth();
  const [policy, setPolicy] = useState<LifecycleStagePolicy[] | null>(null);

  useEffect(() => {
    let cancelled = false;
    policyCache ??= lifecycleApi.policy(token).catch((err) => {
      policyCache = null;
      throw err;
    });
    policyCache.then((p) => !cancelled && setPolicy(p)).catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [token]);

  return policy;
}

/**
 * Lifecycle states for several projects (list views: pending requests, evidence gaps). One request
 * per project - the API has no batch form - so callers pass a bounded list. Missing entries mean
 * "not loaded"; the stage itself always comes from Project.status.
 */
export function useLifecycleStates(projectIds: string[], version: unknown = 0): Map<string, ProjectLifecycleState> {
  const { token } = useAuth();
  const [states, setStates] = useState<Map<string, ProjectLifecycleState>>(() => new Map());
  const key = projectIds.join(',');

  useEffect(() => {
    let cancelled = false;
    const ids = key ? key.split(',') : [];
    Promise.allSettled(ids.map((id) => lifecycleApi.get(id, token))).then((results) => {
      if (cancelled) return;
      const next = new Map<string, ProjectLifecycleState>();
      results.forEach((r) => r.status === 'fulfilled' && next.set(r.value.projectId, r.value));
      setStates(next);
    });
    return () => {
      cancelled = true;
    };
  }, [key, token, version]);

  return states;
}
