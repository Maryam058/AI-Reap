import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { projectsApi } from '../api/projects';
import type { Project } from '../api/types';
import { deriveSdlc, SDLC_PHASES } from '../lib/sdlc';
import { formatFullDateTime, formatRelativeTime, statusLabel, statusVariant } from '../lib/format';
import { PageHeader } from '../components/ui/PageHeader';
import { EmptyState } from '../components/ui/EmptyState';
import { ErrorState } from '../components/ui/ErrorState';
import { ProjectGridSkeleton } from '../components/ui/Skeletons';
import { ProgressBar } from '../components/ui/ProgressBar';
import { ProjectCard } from '../components/ProjectCard';
import { ProjectActionsMenu } from '../components/ProjectActionsMenu';
import { IconFolder, IconGrid, IconPlus, IconSearch } from '../components/icons';

type SortKey = 'updated' | 'created' | 'name' | 'progress';
type View = 'cards' | 'table';

const SORTS: Record<SortKey, { label: string; compare: (a: Project, b: Project) => number }> = {
  updated: { label: 'Recently updated', compare: (a, b) => +new Date(b.updatedAt) - +new Date(a.updatedAt) },
  created: { label: 'Newest first', compare: (a, b) => +new Date(b.createdAt) - +new Date(a.createdAt) },
  name: { label: 'Name (A–Z)', compare: (a, b) => a.name.localeCompare(b.name) },
  progress: { label: 'SDLC progress', compare: (a, b) => deriveSdlc(b.status).percent - deriveSdlc(a.status).percent },
};

function loadView(): View {
  try {
    return localStorage.getItem('aireap.projects-view') === 'table' ? 'table' : 'cards';
  } catch {
    return 'cards';
  }
}

export function ProjectsPage() {
  const { token, hasRole } = useAuth();
  const canCreate = hasRole('Administrator') || hasRole('BusinessAnalyst');
  const [params, setParams] = useSearchParams();

  const [projects, setProjects] = useState<Project[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [view, setView] = useState<View>(loadView);

  const query = params.get('q') ?? '';
  const phase = params.get('phase') ?? 'all';
  const sort = (params.get('sort') as SortKey) in SORTS ? (params.get('sort') as SortKey) : 'updated';

  const load = useCallback(async () => {
    setFailed(false);
    try {
      setProjects(await projectsApi.list(token));
    } catch {
      setFailed(true);
    }
  }, [token]);

  useEffect(() => {
    void load();
  }, [load]);

  const setParam = (key: string, value: string, fallback: string) =>
    setParams(
      (p) => {
        const next = new URLSearchParams(p);
        if (value === fallback || value === '') next.delete(key);
        else next.set(key, value);
        return next;
      },
      { replace: true },
    );

  const changeView = (v: View) => {
    setView(v);
    try {
      localStorage.setItem('aireap.projects-view', v);
    } catch {
      // best effort
    }
  };

  const visible = useMemo(() => {
    if (!projects) return [];
    const q = query.trim().toLowerCase();
    return projects
      .filter((p) => {
        if (q && !`${p.name} ${p.description ?? ''} ${p.domain ?? ''}`.toLowerCase().includes(q)) return false;
        if (phase === 'all') return true;
        const current = deriveSdlc(p.status).current;
        return phase === 'completed' ? current === null : current?.key === phase;
      })
      .sort(SORTS[sort].compare);
  }, [projects, query, phase, sort]);

  const onUpdated = (updated: Project) => setProjects((list) => list?.map((p) => (p.id === updated.id ? updated : p)) ?? null);
  const filtering = query.trim() !== '' || phase !== 'all';
  const newButton = canCreate && (
    <Link to="/projects/new" className="btn btn-primary">
      <IconPlus width={16} height={16} /> New Project
    </Link>
  );

  return (
    <>
      <PageHeader title="Projects" subtitle="Manage your requirements engineering and SDLC projects." actions={newButton} />

      {failed ? (
        <ErrorState title="Unable to load projects" description="We couldn't load your projects right now." onRetry={load} />
      ) : projects === null ? (
        <ProjectGridSkeleton count={6} />
      ) : projects.length === 0 ? (
        <EmptyState
          icon={<IconFolder width={22} height={22} />}
          title="Create your first project"
          description={
            canCreate
              ? 'A project holds everything for one system: requirements, design, tasks, tests and the traceability between them. Create one to start the SDLC workflow.'
              : 'No projects have been created yet. Ask an administrator or business analyst to create one.'
          }
          action={newButton}
        />
      ) : (
        <>
          <div className="toolbar" role="group" aria-label="Filter and sort projects">
            <div className="toolbar-search">
              <IconSearch width={15} height={15} />
              <input type="search" value={query} onChange={(e) => setParam('q', e.target.value, '')} placeholder="Search by name or description…" aria-label="Search projects" />
            </div>
            <label className="toolbar-select">
              <span>Phase</span>
              <select value={phase} onChange={(e) => setParam('phase', e.target.value, 'all')}>
                <option value="all">All phases</option>
                {SDLC_PHASES.map((p) => (
                  <option key={p.key} value={p.key}>
                    {p.navLabel}
                  </option>
                ))}
                <option value="completed">Completed</option>
              </select>
            </label>
            <label className="toolbar-select">
              <span>Sort</span>
              <select value={sort} onChange={(e) => setParam('sort', e.target.value, 'updated')}>
                {(Object.keys(SORTS) as SortKey[]).map((k) => (
                  <option key={k} value={k}>
                    {SORTS[k].label}
                  </option>
                ))}
              </select>
            </label>
            <div className="view-toggle" role="group" aria-label="View">
              <button type="button" className={view === 'cards' ? 'active' : ''} aria-pressed={view === 'cards'} onClick={() => changeView('cards')} title="Card view" aria-label="Card view">
                <IconGrid width={16} height={16} />
              </button>
              <button type="button" className={view === 'table' ? 'active' : ''} aria-pressed={view === 'table'} onClick={() => changeView('table')} title="Table view" aria-label="Table view">
                <TableIcon />
              </button>
            </div>
          </div>
          <p className="result-count" role="status">
            {visible.length} of {projects.length} {projects.length === 1 ? 'project' : 'projects'}
          </p>

          {visible.length === 0 ? (
            <EmptyState
              icon={<IconSearch width={20} height={20} />}
              title="No projects match"
              description="Try a different search term or clear the phase filter."
              action={
                filtering && (
                  <button type="button" className="btn btn-secondary" onClick={() => setParams({}, { replace: true })}>
                    Clear filters
                  </button>
                )
              }
            />
          ) : view === 'cards' ? (
            <div className="project-grid">
              {visible.map((p) => (
                <ProjectCard key={p.id} project={p} onUpdated={onUpdated} />
              ))}
            </div>
          ) : (
            <div className="table-scroll panel flush">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col">Project</th>
                    <th scope="col">Status</th>
                    <th scope="col">SDLC progress</th>
                    <th scope="col">Updated</th>
                    <th scope="col">Created</th>
                    <th scope="col" className="col-actions">
                      <span className="sr-only">Actions</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {visible.map((p) => {
                    const sdlc = deriveSdlc(p.status);
                    return (
                      <tr key={p.id}>
                        <td className="cell-project">
                          <Link to={`/projects/${p.id}`}>{p.name}</Link>
                          <span className="muted clamp-1">{p.description || 'No description yet.'}</span>
                        </td>
                        <td>
                          <span className={`status-pill tone-${statusVariant(p.status)}`}>{statusLabel(p.status)}</span>
                        </td>
                        <td className="cell-progress">
                          <ProgressBar percent={sdlc.percent} />
                          <span>
                            {sdlc.percent}% · {sdlc.current?.label ?? 'Complete'}
                          </span>
                        </td>
                        <td>{formatRelativeTime(p.updatedAt)}</td>
                        <td>{formatFullDateTime(p.createdAt)}</td>
                        <td className="col-actions">
                          <ProjectActionsMenu project={p} onUpdated={onUpdated} />
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </>
  );
}

function TableIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <rect x="3" y="4" width="18" height="16" rx="2" />
      <path d="M3 10h18M3 15h18M9 4v16" />
    </svg>
  );
}
