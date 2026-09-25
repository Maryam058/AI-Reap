import { useEffect, useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { projectsApi } from '../api/projects';
import { roleLabel } from '../lib/lifecycle';

const TTL_MS = 5 * 60_000;
const cache = new Map<string, { at: number; names: Promise<Map<string, string>> }>();

/**
 * Display names of a project's members, from the existing GET /api/projects/{id}/members (open to
 * every project member). One request per project, shared by every caller for a few minutes.
 * Users who aren't members (e.g. an Administrator acting across projects) are not resolved -
 * callers fall back to the role, so nothing beyond the current authorization is revealed.
 */
export function useMemberNames(projectId: string | null | undefined): Map<string, string> {
  const { token } = useAuth();
  const [names, setNames] = useState<Map<string, string>>(() => new Map());

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;
    const hit = cache.get(projectId);
    let entry = hit && Date.now() - hit.at < TTL_MS ? hit : null;
    if (!entry) {
      entry = {
        at: Date.now(),
        names: projectsApi.getMembers(projectId, token).then((members) => new Map(members.map((m) => [m.userId, m.displayName]))),
      };
      entry.names.catch(() => cache.delete(projectId));
      cache.set(projectId, entry);
    }

    entry.names.then((n) => !cancelled && setNames(n)).catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [projectId, token]);

  return names;
}

/** "You", "Dana Lee (Developer)", or just the role when the user can't be resolved. */
export function personLabel(userId: string | null | undefined, role: string | null | undefined, names: Map<string, string>, me: string | null): string {
  if (userId && userId === me) return role ? `You (${roleLabel(role)})` : 'You';
  const name = userId ? names.get(userId) : undefined;
  if (name && role) return `${name} (${roleLabel(role)})`;
  return name ?? (role ? roleLabel(role) : 'Someone');
}
