import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { projectsApi } from '../api/projects';
import { ROLE_LABELS, type ProjectMember } from '../api/types';
import { roleLabel } from '../lib/lifecycle';
import { phasePath } from '../lib/workAreas';
import { Avatar } from './ui/Avatar';
import { IconUser } from './icons';

const PREVIEW = 6;
// Members listed in the order the lifecycle hands work between roles; unknown roles last.
const ROLE_ORDER = Object.keys(ROLE_LABELS);
const rank = (role: string | null) => {
  const i = role ? ROLE_ORDER.indexOf(role) : -1;
  return i < 0 ? ROLE_ORDER.length : i;
};

/**
 * Read-only summary of who has access to the project (its members) and under which role, from the
 * existing GET /api/projects/{id}/members. Members are managed on the Members tab; stakeholders are
 * a separate list and never appear here.
 */
export function ProjectTeamCard({ projectId }: { projectId: string }) {
  const { token, userId } = useAuth();
  const [members, setMembers] = useState<ProjectMember[] | null>(null);
  const [failed, setFailed] = useState(false);

  const load = useCallback(async () => {
    setFailed(false);
    try {
      setMembers(await projectsApi.getMembers(projectId, token));
    } catch {
      setFailed(true);
    }
  }, [projectId, token]);

  useEffect(() => {
    void load();
  }, [load]);

  const sorted = members ? [...members].sort((a, b) => rank(a.role) - rank(b.role) || a.displayName.localeCompare(b.displayName)) : null;
  const membersPath = `${phasePath(projectId, 'gathering')}?tab=members`;

  return (
    <section className="panel" aria-labelledby="team-title">
      <header className="panel-head tight">
        <h2 id="team-title" className="panel-title">
          Project team
        </h2>
        <Link to={membersPath} className="btn btn-ghost btn-sm">
          View members
        </Link>
      </header>
      <p className="field-hint team-hint">People who can open this project, and the role they work under.</p>
      {failed ? (
        <p className="muted">
          The project team is unavailable right now.{' '}
          <button type="button" className="link-button" onClick={() => void load()}>
            Try again
          </button>
        </p>
      ) : !sorted ? (
        <div className="skeleton skeleton-line" />
      ) : sorted.length === 0 ? (
        <p className="muted">
          <IconUser width={14} height={14} /> No members have been added yet.
        </p>
      ) : (
        <>
          <ul className="mini-people team-list">
            {sorted.slice(0, PREVIEW).map((m) => (
              <li key={m.id} className={m.isActive ? undefined : 'is-inactive'}>
                <Avatar name={m.displayName} />
                <span>
                  {m.displayName}
                  {m.userId === userId && <span className="muted"> (you)</span>}
                  <small>
                    {roleLabel(m.role)}
                    {!m.isActive && ' · deactivated'}
                  </small>
                </span>
              </li>
            ))}
          </ul>
          {sorted.length > PREVIEW && (
            <Link to={membersPath} className="team-more">
              View all {sorted.length} members
            </Link>
          )}
        </>
      )}
    </section>
  );
}
