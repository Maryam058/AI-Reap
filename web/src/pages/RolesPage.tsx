import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { rolesApi } from '../api/roles';
import { ROLES, ROLE_LABELS } from '../api/types';
import type { CapabilityGroup } from '../api/types';
import { PageHeader } from '../components/ui/PageHeader';
import { PanelSkeleton } from '../components/ui/Skeletons';
import { ErrorState } from '../components/ui/ErrorState';
import { IconCheck, IconUsers } from '../components/icons';

// Read-only reference, fetched from GET /api/roles/matrix (RolesController), which reflects the
// API's actual [Authorize(Roles = …)] attributes rather than mirroring them by hand here - a hand
// mirror can silently drift from what the API really enforces (it already had once: the project
// membership endpoints were added to the API without ever being added to this page).
export function RolesPage() {
  const { token } = useAuth();
  const [groups, setGroups] = useState<CapabilityGroup[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setFailed(false);
    rolesApi
      .getMatrix(token)
      .then((data) => !cancelled && setGroups(data))
      .catch(() => !cancelled && setFailed(true));
    return () => {
      cancelled = true;
    };
  }, [token, attempt]);

  const header = (
    <PageHeader
      title="Roles & permissions"
      subtitle="What each role can do. Roles are assigned to people on the Users page."
      actions={
        <Link to="/admin/users" className="btn btn-secondary">
          <IconUsers width={16} height={16} /> Manage users
        </Link>
      }
    />
  );

  if (failed) {
    return (
      <>
        {header}
        <ErrorState title="Unable to load roles & permissions" description="We couldn't load the current permission rules right now." onRetry={() => setAttempt((n) => n + 1)} />
      </>
    );
  }

  if (!groups) {
    return (
      <>
        {header}
        <PanelSkeleton />
      </>
    );
  }

  return (
    <>
      {header}
      <section className="panel flush">
        <div className="table-scroll">
          <table className="data-table roles-matrix">
            <thead>
              <tr>
                <th scope="col">Capability</th>
                {ROLES.map((r) => (
                  <th key={r} scope="col" className="num">
                    {ROLE_LABELS[r]}
                  </th>
                ))}
              </tr>
            </thead>
            {groups.map((g) => (
              <tbody key={g.group}>
                <tr>
                  <th scope="rowgroup" colSpan={ROLES.length + 1} className="eyebrow">
                    {g.group}
                  </th>
                </tr>
                {g.items.map((item) => (
                  <tr key={item.label}>
                    <td>{item.label}</td>
                    {ROLES.map((r) => (
                      <td key={r} className="num">
                        {item.roles.includes(r) ? (
                          <span className="matrix-cell completed" role="img" aria-label={`${ROLE_LABELS[r]} can`}>
                            <IconCheck width={12} height={12} strokeWidth={3} />
                          </span>
                        ) : (
                          <span className="muted" aria-label={`${ROLE_LABELS[r]} cannot`}>
                            –
                          </span>
                        )}
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            ))}
          </table>
        </div>
      </section>
    </>
  );
}
