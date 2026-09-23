import { useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { artifactsApi } from '../api/artifacts';
import { dashboardApi } from '../api/dashboard';
import { projectsApi } from '../api/projects';
import { traceabilityApi } from '../api/traceability';
import { ARTIFACT_TYPE_VALUES, type ArtifactSummary, type Project, type TraceabilityRow } from '../api/types';
import { deriveSdlc, SDLC_PHASES } from '../lib/sdlc';
import { BUCKET_COLOR, BUCKET_LABEL, BUCKET_ORDER, countBuckets, isRequirement, weeklyCumulative } from '../lib/requirements';
import { testCoveragePercent } from '../lib/coverage';
import { firstName, formatFullDate, formatRelativeTime, greeting, statusLabel, statusVariant } from '../lib/format';
import { PageHeader } from '../components/ui/PageHeader';
import { EmptyState } from '../components/ui/EmptyState';
import { ErrorState } from '../components/ui/ErrorState';
import { MetricGridSkeleton, PanelSkeleton } from '../components/ui/Skeletons';
import { MetricCard } from '../components/ui/MetricCard';
import { AreaChart, BarList, ColumnChart, DonutChart, RingGauge, StackedBars, type Slice } from '../components/ui/Charts';
import { SdlcDots } from '../components/SdlcStepper';
import { PHASE_STATE_LABEL } from '../lib/sdlc';
import { IconAlert, IconCheck, IconClock, IconFolder, IconPlus, IconTrendUp, IconDocument, IconRocket, IconShieldCheck } from '../components/icons';

/** Per-project data used for the workspace-wide charts. Fields are null if that request failed. */
interface ProjectData {
  project: Project;
  artifacts: ArtifactSummary[] | null;
  stakeholderCount: number | null;
  matrix: TraceabilityRow[] | null;
  conflictCount: number | null;
}

const SAMPLE_SIZE = 12;
const PHASE_COLORS = ['var(--chart-p1)', 'var(--chart-p2)', 'var(--chart-p3)', 'var(--chart-p4)', 'var(--chart-p5)', 'var(--chart-p6)', 'var(--chart-p7)'];

export function DashboardPage() {
  const { token, hasRole, displayName } = useAuth();
  const canCreate = hasRole('Administrator') || hasRole('BusinessAnalyst');
  const [projects, setProjects] = useState<Project[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [data, setData] = useState<ProjectData[] | null>(null);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setFailed(false);
    projectsApi
      .list(token)
      .then((list) => {
        if (cancelled) return;
        setProjects(list);
        const sample = [...list].sort((a, b) => +new Date(b.updatedAt) - +new Date(a.updatedAt)).slice(0, SAMPLE_SIZE);
        return Promise.all(
          sample.map(async (project): Promise<ProjectData> => {
            const [a, s, m, d] = await Promise.allSettled([
              artifactsApi.listForProject(project.id, token),
              projectsApi.getStakeholders(project.id, token),
              traceabilityApi.getMatrix(project.id, token),
              dashboardApi.get(project.id, token),
            ]);
            return {
              project,
              artifacts: a.status === 'fulfilled' ? a.value : null,
              stakeholderCount: s.status === 'fulfilled' ? s.value.length : null,
              matrix: m.status === 'fulfilled' ? m.value : null,
              conflictCount: d.status === 'fulfilled' ? d.value.conflictCount : null,
            };
          }),
        ).then((rows) => !cancelled && setData(rows));
      })
      .catch(() => !cancelled && setFailed(true));
    return () => {
      cancelled = true;
    };
  }, [token, attempt]);

  const stats = useMemo(() => {
    if (!projects) return null;
    const sdlc = projects.map((p) => ({ project: p, sdlc: deriveSdlc(p.status) }));
    const byPhase = SDLC_PHASES.map((ph) => ({ phase: ph, count: sdlc.filter((s) => s.sdlc.current?.key === ph.key).length }));
    const completed = sdlc.filter((s) => s.sdlc.finished).length;
    const avg = sdlc.length ? Math.round(sdlc.reduce((n, s) => n + s.sdlc.percent, 0) / sdlc.length) : 0;

    const rows = data ?? [];
    const allArtifacts = rows.flatMap((r) => r.artifacts ?? []);
    const requirements = allArtifacts.filter(isRequirement);
    const tasks = allArtifacts.filter((a) => a.artifactType === ARTIFACT_TYPE_VALUES.ImplementationTask);
    // A task is "active" until it is implemented, verified or rejected.
    const activeTasks = tasks.filter((t) => t.status !== 4 && t.status !== 5 && t.status !== 6).length;
    const testCases = allArtifacts.filter((a) => a.artifactType === ARTIFACT_TYPE_VALUES.TestCase);
    const nameById = new Map(rows.map((r) => [r.project.id, r.project.name]));
    // Activity is derived from real artifact timestamps; version 1 means it was created, later versions are edits.
    const activity = allArtifacts
      .map((a) => ({ id: a.id, projectId: a.projectId, project: nameById.get(a.projectId) ?? 'Project', code: a.code, title: a.title, created: a.currentVersion <= 1, at: a.updatedAt }))
      .sort((a, b) => +new Date(b.at) - +new Date(a.at))
      .slice(0, 8);
    const typeCount = (...types: number[]) => allArtifacts.filter((a) => types.includes(a.artifactType)).length;
    const T = ARTIFACT_TYPE_VALUES;
    const byType = [
      { key: 'fr', label: 'Functional', value: typeCount(T.FunctionalRequirement), color: 'var(--chart-c1)' },
      { key: 'nfr', label: 'Non-functional', value: typeCount(T.NonFunctionalRequirement), color: 'var(--chart-c4)' },
      { key: 'br', label: 'Business rules', value: typeCount(T.BusinessRule), color: 'var(--chart-c3)' },
      { key: 'us', label: 'User stories', value: typeCount(T.UserStory), color: 'var(--chart-c5)' },
      { key: 'ac', label: 'Acceptance', value: typeCount(T.AcceptanceCriterion), color: 'var(--chart-c8)' },
      { key: 'cq', label: 'Clarifications', value: typeCount(T.ClarificationQuestion), color: 'var(--chart-c9)' },
      { key: 'design', label: 'Design', value: typeCount(T.DesignArtifact, T.ApiSpecification, T.DataEntity), color: 'var(--chart-c6)' },
      { key: 'task', label: 'Tasks', value: tasks.length, color: 'var(--chart-c7)' },
      { key: 'test', label: 'Tests', value: testCases.length, color: 'var(--chart-c2)' },
    ];
    const reqBuckets = countBuckets(requirements);
    const reqByProject = rows
      .map((r) => ({ project: r.project, buckets: countBuckets((r.artifacts ?? []).filter(isRequirement)) }))
      .map((r) => ({ ...r, total: BUCKET_ORDER.reduce((n, b) => n + r.buckets[b], 0) }))
      .filter((r) => r.total > 0)
      .sort((a, b) => b.total - a.total)
      .slice(0, 8);
    const doneTasks = tasks.filter((t) => t.status === 5 || t.status === 6).length;
    const stakeholders = rows.reduce((n, r) => n + (r.stakeholderCount ?? 0), 0);
    const conflicts = rows.reduce((n, r) => n + (r.conflictCount ?? 0), 0);
    const allMatrixRows = rows.flatMap((r) => r.matrix ?? []);
    const pct = (n: number, d: number) => (d > 0 ? Math.round((n / d) * 100) : 0);
    return {
      byType,
      reqByProject,
      approvalRate: pct(reqBuckets.approved + reqBuckets.completed, requirements.length),
      testCoverage: testCoveragePercent(allMatrixRows),
      taskCompletion: pct(doneTasks, tasks.length),
      taskCount: tasks.length,
      doneTasks,
      stakeholders,
      conflicts,
      sdlc,
      byPhase,
      completed,
      active: projects.length - completed,
      avg,
      requirements,
      activeTasks,
      testCases,
      testBuckets: countBuckets(testCases),
      inTesting: sdlc.filter((s) => s.sdlc.current?.key === 'testing').length,
      activity,
      buckets: countBuckets(requirements),
      trend: weeklyCumulative(requirements),
    };
  }, [projects, data]);

  if (failed) return <ErrorState title="Unable to load the dashboard" description="We couldn't load your workspace right now." onRetry={() => setAttempt((n) => n + 1)} />;

  const newButton = canCreate && (
    <Link to="/projects/new" className="btn btn-primary">
      <IconPlus width={16} height={16} /> New Project
    </Link>
  );

  const header = <PageHeader title={`${greeting()}, ${firstName(displayName)}`} subtitle={`${formatFullDate(new Date())} · Project portfolio & SDLC progress`} actions={newButton} />;

  if (!projects || !stats) {
    return (
      <>
        {header}
        <MetricGridSkeleton />
        <PanelSkeleton />
        <PanelSkeleton />
      </>
    );
  }

  if (projects.length === 0) {
    return (
      <>
        {header}
        <EmptyState
          icon={<IconFolder width={22} height={22} />}
          title="Welcome to AI-REAP"
          description={
            canCreate
              ? 'Create a project, then move it through requirements gathering, analysis, validation, design, development, testing and traceability — with AI assistance at every step.'
              : 'No projects exist yet. Ask an administrator or business analyst to create the first one.'
          }
          action={newButton}
        />
      </>
    );
  }

  const sampled = projects.length > SAMPLE_SIZE;
  const dataReady = data !== null;
  const phaseSlices: Slice[] = [
    ...stats.byPhase.map((b, i) => ({ key: b.phase.key, label: b.phase.label, value: b.count, color: PHASE_COLORS[i] })),
    { key: 'completed', label: 'Completed', value: stats.completed, color: 'var(--chart-success)' },
  ];
  const statusSlices: Slice[] = BUCKET_ORDER.map((b) => ({ key: b, label: BUCKET_LABEL[b], value: stats.buckets[b], color: BUCKET_COLOR[b] }));
  const ranked = [...stats.sdlc].sort((a, b) => b.sdlc.percent - a.sdlc.percent || a.project.name.localeCompare(b.project.name));
  const recent = [...projects].sort((a, b) => +new Date(b.updatedAt) - +new Date(a.updatedAt)).slice(0, 5);

  return (
    <>
      {header}

      <div className="metric-grid">
        <MetricCard icon={<IconFolder />} tone="primary" value={projects.length} label="Total projects" />
        <MetricCard icon={<IconRocket />} tone="success" value={stats.active} label="Active projects" trend={`${stats.completed} completed`} />
        <MetricCard icon={<IconTrendUp />} tone="primary" value={`${stats.avg}%`} label="Overall SDLC progress" />
        <MetricCard
          icon={<IconDocument />}
          tone="ai"
          value={dataReady ? stats.requirements.length : '…'}
          label="Requirements"
          trend={dataReady && stats.requirements.length > 0 ? `${Math.round(((stats.buckets.approved + stats.buckets.completed) / stats.requirements.length) * 100)}% approved` : undefined}
        />
        <MetricCard icon={<IconShieldCheck />} tone="success" value={dataReady ? stats.testCases.length : '…'} label="Test cases" />
        <MetricCard icon={<IconClock />} tone="warning" value={dataReady ? stats.activeTasks : '…'} label="Active tasks" />
        <MetricCard icon={<IconAlert />} tone="warning" value={dataReady ? stats.conflicts : '…'} label="Conflicts detected" />
      </div>
      {sampled && dataReady && <p className="footnote">Requirement, test and task figures cover your {SAMPLE_SIZE} most recently updated projects.</p>}

      {dataReady && (
        <div className="insight-grid" aria-label="Analytics highlights">
          <div className="insight-card">
            <RingGauge percent={stats.approvalRate} color="var(--chart-c2)" label="Requirements approved" />
            <div>
              <h3>Approval rate</h3>
              <p>Requirements approved or completed</p>
            </div>
          </div>
          <div className="insight-card">
            <RingGauge percent={stats.testCoverage} color="var(--chart-c5)" label="Test coverage" />
            <div>
              <h3>Test coverage</h3>
              <p>
                {stats.testCases.length} tests for {stats.requirements.length} requirements
              </p>
            </div>
          </div>
          <div className="insight-card">
            <RingGauge percent={stats.taskCompletion} color="var(--chart-c7)" label="Task completion" />
            <div>
              <h3>Task completion</h3>
              <p>
                {stats.doneTasks} of {stats.taskCount} tasks implemented
              </p>
            </div>
          </div>
          <div className="insight-card">
            <RingGauge percent={stats.avg} color="var(--chart-c4)" label="SDLC progress" />
            <div>
              <h3>SDLC progress</h3>
              <p>
                {stats.stakeholders} stakeholders across projects
              </p>
            </div>
          </div>
        </div>
      )}

      <section className="panel" aria-labelledby="matrix-title">
        <header className="panel-head">
          <div>
            <h2 id="matrix-title">Where every project is in the SDLC</h2>
            <p>
              Workspace average: <strong>{stats.avg}%</strong> · {stats.completed} of {projects.length} completed
            </p>
          </div>
          <Link to="/projects" className="btn btn-ghost btn-sm">
            All projects
          </Link>
        </header>
        <div className="table-scroll">
          <table className="sdlc-matrix">
            <thead>
              <tr>
                <th scope="col">Project</th>
                {SDLC_PHASES.map((p) => (
                  <th key={p.key} scope="col" title={p.navLabel}>
                    {p.label}
                  </th>
                ))}
                <th scope="col" className="num">
                  Progress
                </th>
              </tr>
            </thead>
            <tbody>
              {ranked.map(({ project, sdlc }) => (
                <tr key={project.id}>
                  <th scope="row">
                    <Link to={`/projects/${project.id}`}>{project.name}</Link>
                  </th>
                  {sdlc.phases.map((ph) => (
                    <td key={ph.key}>
                      <span className={`matrix-cell ${ph.state}`} role="img" aria-label={`${ph.label}: ${PHASE_STATE_LABEL[ph.state]}`} title={`${ph.label}: ${PHASE_STATE_LABEL[ph.state]}`}>
                        {ph.state === 'completed' ? <IconCheck width={12} height={12} strokeWidth={3} /> : ph.state === 'current' ? <span className="dot" /> : null}
                      </span>
                    </td>
                  ))}
                  <td className="num">
                    <strong>{sdlc.percent}%</strong>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <ul className="matrix-legend" aria-label="Legend">
          <li>
            <span className="matrix-cell completed">
              <IconCheck width={12} height={12} strokeWidth={3} />
            </span>
            Completed
          </li>
          <li>
            <span className="matrix-cell current">
              <span className="dot" />
            </span>
            In progress
          </li>
          <li>
            <span className="matrix-cell upcoming" />
            Upcoming
          </li>
        </ul>
      </section>

      <div className="chart-grid">
        <section className="panel" aria-labelledby="dist-title">
          <header className="panel-head tight">
            <div>
              <h2 id="dist-title" className="panel-title">
                Projects by current phase
              </h2>
            </div>
          </header>
          <DonutChart slices={phaseSlices} centerValue={projects.length} centerLabel="projects" label="Projects by current SDLC phase" />
        </section>

        <section className="panel" aria-labelledby="prog-title">
          <header className="panel-head tight">
            <div>
              <h2 id="prog-title" className="panel-title">
                Project progress
              </h2>
            </div>
          </header>
          <BarList
            label="SDLC progress by project"
            max={100}
            items={ranked.slice(0, 8).map(({ project, sdlc }) => ({ key: project.id, label: project.name, value: sdlc.percent, display: `${sdlc.percent}%`, to: `/projects/${project.id}` }))}
          />
          {ranked.length > 8 && <p className="footnote">Showing the 8 furthest along.</p>}
        </section>

        <section className="panel" aria-labelledby="status-title">
          <header className="panel-head tight">
            <div>
              <h2 id="status-title" className="panel-title">
                Requirements by status
              </h2>
            </div>
          </header>
          {!dataReady ? (
            <div className="skeleton skeleton-panel" />
          ) : stats.requirements.length === 0 ? (
            <EmptyState compact icon={<IconDocument width={18} height={18} />} title="No requirements yet" description="Requirement statuses appear once requirements are generated in a project." />
          ) : (
            <DonutChart slices={statusSlices} centerValue={stats.requirements.length} centerLabel="requirements" label="Requirements by status" />
          )}
        </section>

        <section className="panel" aria-labelledby="trend-title">
          <header className="panel-head tight">
            <div>
              <h2 id="trend-title" className="panel-title">
                Requirements over time
              </h2>
              <p className="panel-sub">Cumulative requirements created, by week</p>
            </div>
          </header>
          {!dataReady ? (
            <div className="skeleton skeleton-panel" />
          ) : stats.trend.length >= 2 ? (
            <AreaChart points={stats.trend} label="Cumulative requirements created per week" unit="requirements" />
          ) : (
            <EmptyState
              compact
              icon={<IconTrendUp width={18} height={18} />}
              title="Not enough history yet"
              description="The trend appears once requirements have been created across more than one week."
            />
          )}
        </section>
      </div>

      <div className="chart-grid">
        <section className="panel" aria-labelledby="type-title">
          <header className="panel-head tight">
            <div>
              <h2 id="type-title" className="panel-title">
                Artifacts by type
              </h2>
              <p className="panel-sub">Everything generated across your projects</p>
            </div>
          </header>
          {!dataReady ? (
            <div className="skeleton skeleton-panel" />
          ) : stats.byType.every((t) => t.value === 0) ? (
            <EmptyState compact icon={<IconDocument width={18} height={18} />} title="No artifacts yet" description="Artifacts appear once a project starts generating requirements and designs." />
          ) : (
            <ColumnChart items={stats.byType} label="Artifacts by type" />
          )}
        </section>

        <section className="panel" aria-labelledby="reqproj-title">
          <header className="panel-head tight">
            <div>
              <h2 id="reqproj-title" className="panel-title">
                Requirement status by project
              </h2>
              <p className="panel-sub">Top projects by requirement count</p>
            </div>
          </header>
          {!dataReady ? (
            <div className="skeleton skeleton-panel" />
          ) : stats.reqByProject.length === 0 ? (
            <EmptyState compact icon={<IconDocument width={18} height={18} />} title="No requirements yet" description="Per-project status appears once requirements are generated." />
          ) : (
            <StackedBars
              label="Requirement status by project"
              legend={BUCKET_ORDER.map((b) => ({ key: b, label: BUCKET_LABEL[b], color: BUCKET_COLOR[b] }))}
              rows={stats.reqByProject.map((r) => ({
                key: r.project.id,
                label: r.project.name,
                to: `/projects/${r.project.id}`,
                segments: BUCKET_ORDER.map((b) => ({ key: b, label: BUCKET_LABEL[b], value: r.buckets[b], color: BUCKET_COLOR[b] })),
              }))}
            />
          )}
        </section>
      </div>

      <div className="chart-grid">
        <section className="panel" aria-labelledby="qa-title">
          <header className="panel-head tight">
            <div>
              <h2 id="qa-title" className="panel-title">
                Testing &amp; QA status
              </h2>
              <p className="panel-sub">
                {stats.inTesting} {stats.inTesting === 1 ? 'project is' : 'projects are'} in the testing phase
              </p>
            </div>
          </header>
          {!dataReady ? (
            <div className="skeleton skeleton-panel" />
          ) : stats.testCases.length === 0 ? (
            <EmptyState compact icon={<IconShieldCheck width={18} height={18} />} title="No test cases yet" description="Test cases appear once they are derived from requirements in a project." />
          ) : (
            <BarList
              label="Test cases by status"
              max={stats.testCases.length}
              items={BUCKET_ORDER.map((b) => ({ key: b, label: BUCKET_LABEL[b], value: stats.testBuckets[b], display: String(stats.testBuckets[b]), color: BUCKET_COLOR[b] }))}
            />
          )}
        </section>

        <section className="panel" aria-labelledby="activity-title">
          <header className="panel-head tight">
            <div>
              <h2 id="activity-title" className="panel-title">
                Recent activity
              </h2>
            </div>
          </header>
          {!dataReady ? (
            <div className="skeleton skeleton-panel" />
          ) : stats.activity.length === 0 ? (
            <EmptyState compact icon={<IconClock width={18} height={18} />} title="No activity yet" description="Changes to requirements, design, tasks and tests show up here." />
          ) : (
            <ul className="activity-list">
              {stats.activity.map((a) => (
                <li key={a.id}>
                  <Link to={`/projects/${a.projectId}`} className="activity-link">
                    <span className="activity-text">
                      <strong>{a.code}</strong> {a.title}
                    </span>
                    <span className="activity-meta">
                      {a.created ? 'Created' : 'Updated'} in {a.project} · {formatRelativeTime(a.at)}
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>

      <section className="panel" aria-labelledby="recent-title">
        <header className="panel-head">
          <div>
            <h2 id="recent-title">Recent projects</h2>
          </div>
          <Link to="/projects" className="btn btn-ghost btn-sm">
            Manage projects
          </Link>
        </header>
        <ul className="recent-list">
          {recent.map((p) => {
            const sdlc = deriveSdlc(p.status);
            return (
              <li key={p.id}>
                <Link to={`/projects/${p.id}`} className="recent-link">
                  <span className="recent-name">{p.name}</span>
                  <span className={`status-pill tone-${statusVariant(p.status)}`}>{statusLabel(p.status)}</span>
                  <SdlcDots phases={sdlc.phases} />
                  <span className="recent-pct">{sdlc.percent}%</span>
                  <span className="recent-time">{formatRelativeTime(p.updatedAt)}</span>
                </Link>
              </li>
            );
          })}
        </ul>
      </section>
    </>
  );
}
