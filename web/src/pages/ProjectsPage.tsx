import { useEffect, useState, type FormEvent } from 'react';
import { useAuth } from '../auth/AuthContext';
import { apiClient, ApiError } from '../api/client';
import { dashboardApi } from '../api/dashboard';
import type { CreateProjectRequest, Project, ProjectDashboard } from '../api/types';
import { Layout } from '../components/Layout';
import { ProjectCard } from '../components/ProjectCard';
import { MetricCard } from '../components/ui/MetricCard';
import { EmptyState } from '../components/ui/EmptyState';
import { PageHeader } from '../components/ui/PageHeader';
import { MetricGridSkeleton, ProjectGridSkeleton } from '../components/ui/Skeletons';
import { firstName, formatFullDate, formatRelativeTime } from '../lib/format';
import {
  IconAlert,
  IconCheckCircle,
  IconClock,
  IconFolder,
  IconInbox,
  IconPlus,
  IconSparkles,
} from '../components/icons';

interface ActivityEntry {
  projectId: string;
  projectName: string;
  code: string;
  title: string;
  status: string;
  updatedAt: string;
}

interface QualityTotals {
  requirements: number;
  approved: number;
  pendingReview: number;
  rejected: number;
  openQuestions: number;
}

interface AggregateState {
  loading: boolean;
  totals: QualityTotals | null;
  activity: ActivityEntry[];
}

const AGGREGATE_SAMPLE_SIZE = 12;

export function ProjectsPage() {
  const { token, hasRole, displayName } = useAuth();
  const [projects, setProjects] = useState<Project[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [aggregate, setAggregate] = useState<AggregateState>({ loading: true, totals: null, activity: [] });

  const canCreate = hasRole('Administrator') || hasRole('BusinessAnalyst');

  const [showCreateForm, setShowCreateForm] = useState(false);
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);

  const loadProjects = async () => {
    setLoading(true);
    try {
      const data = await apiClient.get<Project[]>('/api/projects', token);
      setProjects(data);
      setError(null);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to load projects.');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    loadProjects();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (loading) return;
    if (projects.length === 0) {
      setAggregate({ loading: false, totals: null, activity: [] });
      return;
    }

    let cancelled = false;
    setAggregate((a) => ({ ...a, loading: true }));

    const subset = [...projects]
      .sort((a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime())
      .slice(0, AGGREGATE_SAMPLE_SIZE);

    Promise.all(
      subset.map((project) =>
        dashboardApi
          .get(project.id, token)
          .then((data) => ({ project, data }))
          .catch(() => null),
      ),
    ).then((results) => {
      if (cancelled) return;
      const valid = results.filter((r): r is { project: Project; data: ProjectDashboard } => r !== null);

      const totals = valid.reduce<QualityTotals>(
        (acc, { data }) => ({
          requirements: acc.requirements + data.functionalRequirementCount + data.nonFunctionalRequirementCount,
          approved: acc.approved + data.approvedCount,
          pendingReview: acc.pendingReview + data.pendingReviewCount,
          rejected: acc.rejected + data.rejectedCount,
          openQuestions: acc.openQuestions + data.openClarificationCount,
        }),
        { requirements: 0, approved: 0, pendingReview: 0, rejected: 0, openQuestions: 0 },
      );

      const activity = valid
        .flatMap(({ project, data }) =>
          data.recentChanges.map((change) => ({
            projectId: project.id,
            projectName: project.name,
            code: change.code,
            title: change.title,
            status: change.status,
            updatedAt: change.updatedAt,
          })),
        )
        .sort((a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime())
        .slice(0, 6);

      setAggregate({ loading: false, totals, activity });
    });

    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projects, loading, token]);

  const onCreate = async (e: FormEvent) => {
    e.preventDefault();
    setCreating(true);
    setCreateError(null);
    try {
      const request: CreateProjectRequest = { name, description: description || undefined };
      await apiClient.post<Project>('/api/projects', request, token);
      setName('');
      setDescription('');
      setShowCreateForm(false);
      await loadProjects();
    } catch (err) {
      setCreateError(err instanceof ApiError ? err.message : 'Failed to create project.');
    } finally {
      setCreating(false);
    }
  };

  const totalProjects = projects.length;
  const inProgress = projects.filter((p) => p.status >= 1 && p.status <= 6).length;
  const approved = projects.filter((p) => p.status === 4).length;
  const completed = projects.filter((p) => p.status === 7).length;

  const recentProjects = [...projects].sort(
    (a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime(),
  );

  const totals = aggregate.totals;
  const reviewedTotal = totals ? totals.approved + totals.pendingReview + totals.rejected : 0;
  const inProgressReqs = totals ? Math.max(totals.requirements - reviewedTotal, 0) : 0;
  const pct = (n: number) => (totals && totals.requirements > 0 ? (n / totals.requirements) * 100 : 0);

  return (
    <Layout>
      <PageHeader
        title="Dashboard"
        subtitle={`${formatFullDate(new Date())} — welcome back, ${firstName(displayName)}.`}
        actions={
          canCreate && (
            <button className="btn btn-primary" onClick={() => setShowCreateForm((v) => !v)}>
              <IconPlus />
              New Project
            </button>
          )
        }
      />

      {canCreate && showCreateForm && (
        <form onSubmit={onCreate} className="card dash-create-card">
          <h2>New project</h2>
          <label>
            Name
            <input value={name} onChange={(e) => setName(e.target.value)} required autoFocus />
          </label>
          <label>
            Description
            <textarea value={description} onChange={(e) => setDescription(e.target.value)} rows={2} />
          </label>
          {createError && (
            <div className="alert alert-danger">
              <IconAlert />
              <span>{createError}</span>
            </div>
          )}
          <div className="dash-create-actions">
            <button type="submit" disabled={creating}>
              {creating ? 'Creating…' : 'Create project'}
            </button>
            <button type="button" className="btn btn-ghost" onClick={() => setShowCreateForm(false)}>
              Cancel
            </button>
          </div>
        </form>
      )}

      {error && (
        <div className="alert alert-danger" style={{ marginBottom: '1.5rem' }}>
          <IconAlert />
          <span>{error}</span>
        </div>
      )}

      {loading ? (
        <MetricGridSkeleton />
      ) : (
        <div className="metric-grid">
          <MetricCard icon={<IconFolder />} tone="primary" value={totalProjects} label="Total projects" />
          <MetricCard icon={<IconClock />} tone="warning" value={inProgress} label="In progress" />
          <MetricCard icon={<IconCheckCircle />} tone="success" value={approved} label="Approved" />
          <MetricCard icon={<IconSparkles />} tone="ai" value={completed} label="Completed" />
        </div>
      )}

      <div className="dash-columns">
        <section>
          <div className="section-heading">
            <h2>Recent Projects</h2>
            {recentProjects.length > 0 && <span className="section-heading-hint">{recentProjects.length} total</span>}
          </div>

          {loading ? (
            <ProjectGridSkeleton />
          ) : recentProjects.length === 0 ? (
            <EmptyState
              icon={<IconFolder />}
              title="Create your first project"
              description={
                canCreate
                  ? 'Spin up a project to start capturing requirements and let AI-REAP help structure, analyze, and trace them through delivery.'
                  : 'No projects have been created yet. Ask an administrator or business analyst to create one.'
              }
              action={
                canCreate && (
                  <button className="btn btn-primary" onClick={() => setShowCreateForm(true)}>
                    <IconPlus />
                    New Project
                  </button>
                )
              }
            />
          ) : (
            <div className="project-grid">
              {recentProjects.map((p) => (
                <ProjectCard key={p.id} project={p} />
              ))}
            </div>
          )}
        </section>

        <aside>
          <div className="side-panel">
            <div className="side-panel-header">
              <span className="side-panel-icon tone-primary">
                <IconSparkles />
              </span>
              <h3>Requirement Quality</h3>
            </div>

            {aggregate.loading ? (
              <>
                <div className="skeleton skeleton-line" style={{ height: 8, marginBottom: '0.9rem' }} />
                <div className="skeleton skeleton-line" style={{ width: '70%' }} />
                <div className="skeleton skeleton-line" style={{ width: '55%' }} />
              </>
            ) : !totals || totals.requirements === 0 ? (
              <EmptyState
                compact
                icon={<IconSparkles />}
                title="No requirements yet"
                description="Quality insights will appear once your team generates requirements with AI."
              />
            ) : (
              <>
                <div className="quality-meter">
                  <div className="quality-meter-segment approved" style={{ width: `${pct(totals.approved)}%` }} />
                  <div className="quality-meter-segment pending" style={{ width: `${pct(totals.pendingReview)}%` }} />
                  <div className="quality-meter-segment rejected" style={{ width: `${pct(totals.rejected)}%` }} />
                  <div className="quality-meter-segment open" style={{ width: `${pct(inProgressReqs)}%` }} />
                </div>
                <div className="quality-legend">
                  <span className="quality-legend-item">
                    <span className="quality-legend-dot approved" /> Approved {totals.approved}
                  </span>
                  <span className="quality-legend-item">
                    <span className="quality-legend-dot pending" /> In review {totals.pendingReview}
                  </span>
                  <span className="quality-legend-item">
                    <span className="quality-legend-dot rejected" /> Rejected {totals.rejected}
                  </span>
                </div>
                <div className="quality-stat-row">
                  <span>Total requirements</span>
                  <strong>{totals.requirements}</strong>
                </div>
                <div className="quality-stat-row">
                  <span>Open clarifications</span>
                  <strong>{totals.openQuestions}</strong>
                </div>
              </>
            )}
          </div>

          <div className="side-panel">
            <div className="side-panel-header">
              <span className="side-panel-icon tone-ai">
                <IconSparkles />
              </span>
              <h3>Recent Activity</h3>
            </div>

            {aggregate.loading ? (
              <>
                <div className="skeleton skeleton-line" />
                <div className="skeleton skeleton-line" style={{ width: '80%' }} />
                <div className="skeleton skeleton-line" style={{ width: '60%' }} />
              </>
            ) : aggregate.activity.length === 0 ? (
              <EmptyState
                compact
                icon={<IconInbox />}
                title="No activity yet"
                description="Changes to requirements and AI-generated artifacts will show up here."
              />
            ) : (
              <ul className="activity-list">
                {aggregate.activity.map((item, i) => (
                  <li className="activity-item" key={`${item.projectId}-${item.code}-${i}`}>
                    <span className="activity-dot">
                      <IconSparkles />
                    </span>
                    <div className="activity-body">
                      <div className="activity-title">
                        <span className="code">{item.code}</span>
                        {item.title}
                      </div>
                      <div className="activity-meta">
                        {item.projectName} · {item.status} · {formatRelativeTime(item.updatedAt)}
                      </div>
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </aside>
      </div>
    </Layout>
  );
}
