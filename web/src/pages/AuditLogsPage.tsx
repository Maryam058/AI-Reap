import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { projectsApi } from '../api/projects';
import type { Project } from '../api/types';
import { PageHeader } from '../components/ui/PageHeader';
import { EmptyState } from '../components/ui/EmptyState';
import { ErrorState } from '../components/ui/ErrorState';
import { AuditTrailPanel } from '../components/AuditTrailPanel';
import { IconClock } from '../components/icons';

/** The audit trail is recorded per project, so this page is a project picker around the existing panel. */
export function AuditLogsPage() {
  const { token } = useAuth();
  const [params, setParams] = useSearchParams();
  const [projects, setProjects] = useState<Project[] | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    projectsApi
      .list(token)
      .then((list) => !cancelled && setProjects(list))
      .catch(() => !cancelled && setFailed(true));
    return () => {
      cancelled = true;
    };
  }, [token]);

  const selected = params.get('project') ?? projects?.[0]?.id ?? '';

  return (
    <>
      <PageHeader title="Audit logs" subtitle="Every AI operation recorded for a project: what ran, on which model, and when." />
      {failed ? (
        <ErrorState title="Unable to load projects" description="We couldn't load the project list right now." onRetry={() => window.location.reload()} />
      ) : !projects ? (
        <div className="skeleton skeleton-panel" />
      ) : projects.length === 0 ? (
        <EmptyState icon={<IconClock width={22} height={22} />} title="Nothing to audit yet" description="Audit logs appear once a project exists and AI operations have run." />
      ) : (
        <>
          <div className="toolbar">
            <label className="toolbar-select">
              <span>Project</span>
              <select value={selected} onChange={(e) => setParams({ project: e.target.value }, { replace: true })}>
                {projects.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name}
                  </option>
                ))}
              </select>
            </label>
          </div>
          <AuditTrailPanel key={selected} projectId={selected} />
        </>
      )}
    </>
  );
}
