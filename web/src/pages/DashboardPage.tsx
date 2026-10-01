import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { artifactsApi } from '../api/artifacts';
import { dashboardApi } from '../api/dashboard';
import { projectsApi } from '../api/projects';
import { lifecycleApi } from '../api/lifecycle';
import { ARTIFACT_TYPE_VALUES, type ArtifactSummary, type Project, type ProjectLifecycleState } from '../api/types';
import { LAST_STAGE, lifecycleProgress, LIFECYCLE_STAGES, roleLabel, STAGE_STATE_LABEL, stageLabel, stageState } from '../lib/lifecycle';
import { BUCKET_COLOR, BUCKET_LABEL, BUCKET_ORDER, countBuckets, isRequirement, weeklyCumulative } from '../lib/requirements';
import { firstName, formatFullDate, formatRelativeTime, greeting, statusLabel, statusVariant } from '../lib/format';
import { HEALTH_LABEL, HEALTH_ORDER, projectHealth, type ProjectHealth } from '../lib/projectHealth';
import { PageHeader } from '../components/ui/PageHeader';
import { EmptyState } from '../components/ui/EmptyState';
import { ErrorState } from '../components/ui/ErrorState';
import { MetricGridSkeleton, PanelSkeleton } from '../components/ui/Skeletons';
import { MetricCard } from '../components/ui/MetricCard';
import { AreaChart, BarList, ColumnChart, DonutChart, RingGauge, StackedBars, type Slice } from '../components/ui/Charts';
import { LifecycleDots, LifecycleStepper } from '../components/LifecycleStepper';
import {
  IconAlert,
  IconArrowRight,
  IconCheck,
  IconCheckCircle,
  IconClock,
  IconCode,
  IconDocument,
  IconEdit,
  IconFilter,
  IconFolder,
  IconLayers,
  IconLock,
  IconPlus,
  IconRocket,
  IconShieldCheck,
  IconTrendUp,
} from '../components/icons';

/** Per-project data used for the workspace-wide charts. Fields are null if that request failed. */
interface ProjectData {
  project: Project;
  artifacts: ArtifactSummary[] | null;
  stakeholderCount: number | null;
  /** §28 project-wide test coverage (backend dashboard counts; not visibility-filtered). */
  functionalRequirementCount: number | null;
  testedFunctionalRequirementCount: number | null;
  conflictCount: number | null;
  openImpactNoticeCount: number | null;
  /** Pending stage requests and evidence gaps (GET .../lifecycle). */
  lifecycle: ProjectLifecycleState | null;
}

const SAMPLE_SIZE = 12;
// Rows in the dashboard's project table; the full list lives on /projects.
const RECENT_SIZE = 8;
// One colour per lifecycle stage, Draft … Testing, then Completed in the success colour.
const STAGE_COLORS = ['var(--chart-neutral)', 'var(--chart-p1)', 'var(--chart-p2)', 'var(--chart-p3)', 'var(--chart-p4)', 'var(--chart-p5)', 'var(--chart-p6)', 'var(--chart-success)'];

/** Everything the dashboard shows for one project. Never rejects: a failed request leaves its fields null. */
async function loadProjectData(project: Project, token: string | null): Promise<ProjectData> {
  const [a, s, d, l] = await Promise.allSettled([
    artifactsApi.listForProject(project.id, token),
    projectsApi.getStakeholders(project.id, token),
    dashboardApi.get(project.id, token),
    lifecycleApi.get(project.id, token),
  ]);
  return {
    project,
    artifacts: a.status === 'fulfilled' ? a.value : null,
    stakeholderCount: s.status === 'fulfilled' ? s.value.length : null,
    functionalRequirementCount: d.status === 'fulfilled' ? d.value.functionalRequirementCount : null,
    testedFunctionalRequirementCount: d.status === 'fulfilled' ? (d.value.testedFunctionalRequirementCount ?? 0) : null,
    conflictCount: d.status === 'fulfilled' ? d.value.conflictCount : null,
    openImpactNoticeCount: d.status === 'fulfilled' ? (d.value.openImpactNoticeCount ?? 0) : null,
    lifecycle: l.status === 'fulfilled' ? l.value : null,
  };
}

/** Portfolio figures for the projects in scope (all of them, or the one selected in the report filter). */
function buildStats(projects: Project[], data: ProjectData[] | null) {
  // The lifecycle stage is Project.status; progress is across the 8 stages. Nothing here uses agent runs.
  const lifecycle = projects.map((p) => ({ project: p, progress: lifecycleProgress(p.status) }));
  const byStage = LIFECYCLE_STAGES.map((st) => ({ stage: st, count: projects.filter((p) => p.status === st.status).length }));
  const completed = lifecycle.filter((s) => s.progress.finished).length;
  const avg = lifecycle.length ? Math.round(lifecycle.reduce((n, s) => n + s.progress.percent, 0) / lifecycle.length) : 0;

  const rows = data ?? [];
  const lifecycleById = new Map(rows.filter((r) => r.lifecycle).map((r) => [r.project.id, r.lifecycle!]));
  const pendingApprovals = rows
    .filter((r) => r.lifecycle?.pendingTransition)
    .map((r) => ({ project: r.project, request: r.lifecycle!.pendingTransition! }))
    .sort((a, b) => +new Date(a.request.requestedAt) - +new Date(b.request.requestedAt));
  const evidenceGaps = rows.filter((r) => (r.lifecycle?.evidenceGaps.length ?? 0) > 0).length;
  const allArtifacts = rows.flatMap((r) => r.artifacts ?? []);
  const requirements = allArtifacts.filter(isRequirement);
  const tasks = allArtifacts.filter((a) => a.artifactType === ARTIFACT_TYPE_VALUES.ImplementationTask);
  // Planned tasks (not rejected). Whether a task is *done* lives in its work records, which have no
  // workspace-wide endpoint, so the dashboard makes no task-completion claim.
  const plannedTasks = tasks.filter((t) => t.status !== 4).length;
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
  const stakeholders = rows.reduce((n, r) => n + (r.stakeholderCount ?? 0), 0);
  const conflicts = rows.reduce((n, r) => n + (r.conflictCount ?? 0), 0);
  // §22 — downstream artifacts still flagged by an approved upstream change nobody has reviewed yet.
  const impactNotices = rows.reduce((n, r) => n + (r.openImpactNoticeCount ?? 0), 0);
  // Test coverage = FRs with >=1 TestedBy test case / all FRs, summed over the sampled projects.
  const coveredFrs = rows.reduce((n, r) => n + (r.testedFunctionalRequirementCount ?? 0), 0);
  const totalFrs = rows.reduce((n, r) => n + (r.testedFunctionalRequirementCount !== null ? (r.functionalRequirementCount ?? 0) : 0), 0);
  const pct = (n: number, d: number) => (d > 0 ? Math.round((n / d) * 100) : 0);
  // Display-only health per project, from the lifecycle state and dashboard counts loaded above.
  const rowById = new Map(rows.map((r) => [r.project.id, r]));
  const health = new Map(
    projects.map((p) => {
      const r = rowById.get(p.id);
      const signals =
        r && (r.lifecycle || r.conflictCount !== null)
          ? { pendingReview: !!r.lifecycle?.pendingTransition, evidenceGaps: r.lifecycle?.evidenceGaps.length ?? 0, conflicts: r.conflictCount, impactNotices: r.openImpactNoticeCount }
          : null;
      return [p.id, projectHealth(p.status, signals)];
    }),
  );
  const healthCounts = Object.fromEntries(HEALTH_ORDER.map((h) => [h, 0])) as Record<ProjectHealth, number>;
  health.forEach((h) => healthCounts[h.health]++);
  return {
    health,
    healthCounts,
    impactNotices,
    byType,
    reqByProject,
    approvalRate: pct(reqBuckets.approved + reqBuckets.completed, requirements.length),
    testCoverage: pct(coveredFrs, totalFrs),
    coveredFrs,
    totalFrs,
    lifecycleCompletion: pct(completed, projects.length),
    stakeholders,
    conflicts,
    lifecycle,
    lifecycleById,
    pendingApprovals,
    evidenceGaps,
    byStage,
    completed,
    active: projects.length - completed,
    avg,
    requirements,
    plannedTasks,
    testCases,
    testBuckets: countBuckets(testCases),
    inTesting: projects.filter((p) => p.status === 6).length,
    activity,
    buckets: countBuckets(requirements),
    trend: weeklyCumulative(requirements),
  };
}

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
        return Promise.all(sample.map((project) => loadProjectData(project, token))).then((rows) => !cancelled && setData(rows));
      })
      .catch(() => !cancelled && setFailed(true));
    return () => {
      cancelled = true;
    };
  }, [token, attempt]);

  // Report scope: all projects, or one project picked in the filter (kept in ?project= so the view can be shared).
  const [params, setParams] = useSearchParams();
  const requested = params.get('project');
  const scopeProject = (requested && projects?.find((p) => p.id === requested)) || null;
  const setScope = (id: string) => setParams(id ? { project: id } : {}, { replace: true });

  // A project outside the sampled set is loaded on demand when it is picked.
  const [extra, setExtra] = useState<Record<string, ProjectData>>({});
  const needsLoad = scopeProject && data && !data.some((r) => r.project.id === scopeProject.id) && !extra[scopeProject.id] ? scopeProject : null;
  useEffect(() => {
    if (!needsLoad) return;
    let cancelled = false;
    loadProjectData(needsLoad, token).then((row) => !cancelled && setExtra((e) => ({ ...e, [row.project.id]: row })));
    return () => {
      cancelled = true;
    };
  }, [needsLoad, token]);

  const scopedProjects = useMemo(() => (projects && scopeProject ? [scopeProject] : projects), [projects, scopeProject]);
  const scopedData = useMemo(() => {
    if (!data || !scopeProject) return data;
    const row = data.find((r) => r.project.id === scopeProject.id) ?? extra[scopeProject.id];
    return row ? [row] : null;
  }, [data, scopeProject, extra]);
  const stats = useMemo(() => (scopedProjects ? buildStats(scopedProjects, scopedData) : null), [scopedProjects, scopedData]);

  if (failed) return <ErrorState title="Unable to load the dashboard" description="We couldn't load your workspace right now." onRetry={() => setAttempt((n) => n + 1)} />;

  const newButton = canCreate && (
    <Link to="/projects/new" className="btn btn-primary">
      <IconPlus width={16} height={16} /> New Project
    </Link>
  );

  const filter = projects && projects.length > 0 && (
    <label className="dash-filter">
      <IconFilter width={15} height={15} aria-hidden="true" />
      <span className="sr-only">Report scope</span>
      <select value={scopeProject?.id ?? ''} onChange={(e) => setScope(e.target.value)} aria-label="Show the report for">
        <option value="">All projects ({projects.length})</option>
        {[...projects]
          .sort((a, b) => a.name.localeCompare(b.name))
          .map((p) => (
            <option key={p.id} value={p.id}>
              {p.name} · {stageLabel(p.status)}
            </option>
          ))}
      </select>
    </label>
  );

  const header = (
    <PageHeader
      eyebrow={scopeProject ? 'Project progress report' : 'Portfolio overview'}
      title={`${greeting()}, ${firstName(displayName)}`}
      subtitle={scopeProject ? `${formatFullDate(new Date())} · Showing ${scopeProject.name}` : `${formatFullDate(new Date())} · Project portfolio & lifecycle progress`}
      actions={
        projects && projects.length > 0 ? (
          <>
            {filter}
            {scopeProject ? (
              <Link to={`/projects/${scopeProject.id}`} className="btn btn-ghost">
                Open project
              </Link>
            ) : (
              <Link to="/projects" className="btn btn-ghost">
                View all projects
              </Link>
            )}
            {newButton}
          </>
        ) : (
          newButton
        )
      }
    />
  );

  if (!projects || !scopedProjects || !stats) {
    return (
      <div className="dash" aria-busy="true">
        {header}
        <MetricGridSkeleton count={5} />
        <div className="skeleton dash-signals-skeleton" />
        <div className="dash-main">
          <PanelSkeleton />
          <PanelSkeleton />
        </div>
      </div>
    );
  }

  if (projects.length === 0) {
    return (
      <div className="dash">
        {header}
        <EmptyState
          icon={<IconFolder width={22} height={22} />}
          title="Welcome to AI-REAP"
          description={
            canCreate
              ? 'Create a project, then take it through its lifecycle - Draft, Requirements Gathering, Analysis, Design, Approved, Implementation, Testing and Completed - with AI assistance along the way.'
              : "You haven't been added to any project yet. Ask an administrator or business analyst to add you as a project member."
          }
          action={newButton}
        />
      </div>
    );
  }

  const list = scopedProjects;
  const single = scopeProject;
  const sampled = !single && projects.length > SAMPLE_SIZE;
  const dataReady = scopedData !== null;
  const loadingValue = (n: number) => (dataReady ? n : '…');
  const stageSlices: Slice[] = stats.byStage.map((b) => ({ key: String(b.stage.status), label: b.stage.label, value: b.count, color: STAGE_COLORS[b.stage.status] }));
  const statusSlices: Slice[] = BUCKET_ORDER.map((b) => ({ key: b, label: BUCKET_LABEL[b], value: stats.buckets[b], color: BUCKET_COLOR[b] }));
  const ranked = [...stats.lifecycle].sort((a, b) => b.progress.percent - a.progress.percent || a.project.name.localeCompare(b.project.name));
  const recent = [...list].sort((a, b) => +new Date(b.updatedAt) - +new Date(a.updatedAt)).slice(0, RECENT_SIZE);
  const attention = stats.healthCounts.attention;
  const shownHealth = HEALTH_ORDER.filter((h) => h !== 'unknown' || stats.healthCounts.unknown > 0);

  // Single-project report.
  const sp = single ? lifecycleProgress(single.status) : null;
  const sh = single ? (stats.health.get(single.id) ?? { health: 'unknown' as const, reasons: [] }) : null;
  const sl = single ? (scopedData?.[0]?.lifecycle ?? null) : null;
  const sRow = single ? (scopedData?.[0] ?? null) : null;

  return (
    <div className="dash">
      {header}

      {single && sp && sh ? (
        <div className="metric-grid dash-kpis">
          <MetricCard icon={<IconTrendUp />} tone="ai" value={`${sp.percent}%`} label="Lifecycle progress" hint={<Meter percent={sp.percent} done={sp.finished} />} />
          <MetricCard
            icon={<IconLayers />}
            tone={sp.finished ? 'success' : 'primary'}
            value={<span className="metric-value-text">{stageLabel(single.status)}</span>}
            label={sp.finished ? 'Lifecycle completed' : 'Current stage'}
            hint={sp.finished ? `All ${LIFECYCLE_STAGES.length} stages complete` : `Stage ${sp.stageNumber} of ${LIFECYCLE_STAGES.length}`}
          />
          <MetricCard
            icon={sh.health === 'attention' ? <IconAlert /> : <IconCheckCircle />}
            tone={sh.health === 'attention' ? 'warning' : sh.health === 'unknown' ? 'primary' : 'success'}
            value={<span className="metric-value-text">{!dataReady && sh.health === 'unknown' ? '…' : HEALTH_LABEL[sh.health]}</span>}
            label="Health"
            hint={sh.reasons[0] ?? (sh.health === 'on-track' ? 'No open issues' : undefined)}
          />
          <MetricCard
            icon={<IconDocument />}
            tone="success"
            value={dataReady ? `${stats.approvalRate}%` : '…'}
            label="Requirements approved"
            hint={dataReady ? `${stats.buckets.approved + stats.buckets.completed} of ${stats.requirements.length} requirements` : undefined}
          />
          <MetricCard
            icon={<IconShieldCheck />}
            tone="primary"
            value={dataReady ? `${stats.testCoverage}%` : '…'}
            label="Test coverage"
            hint={dataReady ? `${stats.coveredFrs} of ${stats.totalFrs} functional requirements` : undefined}
          />
        </div>
      ) : (
        <div className="metric-grid dash-kpis">
          <MetricCard icon={<IconFolder />} tone="primary" value={list.length} label="Total projects" hint={`Across ${stats.byStage.filter((b) => b.count > 0).length} of ${LIFECYCLE_STAGES.length} lifecycle stages`} />
          <MetricCard icon={<IconRocket />} tone="primary" value={stats.active} label="Active projects" hint={`${stats.inTesting} in Testing`} />
          <MetricCard icon={<IconCheckCircle />} tone="success" value={stats.completed} label="Completed" hint={`${stats.lifecycleCompletion}% of the portfolio`} />
          <MetricCard
            icon={<IconAlert />}
            tone="warning"
            value={loadingValue(attention)}
            label="Need attention"
            hint={!dataReady ? 'Checking projects…' : attention > 0 ? 'Reviews, evidence gaps, conflicts or changes' : 'No open issues'}
          />
          <MetricCard icon={<IconTrendUp />} tone="ai" value={`${stats.avg}%`} label="Average lifecycle progress" hint={<Meter percent={stats.avg} />} />
        </div>
      )}

      <section className="dash-signals" aria-label="Delivery signals">
        <Signal icon={<IconLock />} tone="warning" value={loadingValue(stats.pendingApprovals.length)} label="Awaiting stage approval" />
        <Signal
          icon={<IconDocument />}
          tone="primary"
          value={loadingValue(stats.requirements.length)}
          label="Requirements"
          meta={dataReady && stats.requirements.length > 0 ? `${stats.approvalRate}% approved` : undefined}
        />
        <Signal icon={<IconShieldCheck />} tone="success" value={loadingValue(stats.testCases.length)} label="Test cases" />
        <Signal icon={<IconCode />} tone="primary" value={loadingValue(stats.plannedTasks)} label="Implementation tasks" />
        <Signal icon={<IconAlert />} tone="danger" value={loadingValue(stats.conflicts)} label="Conflicts detected" />
        <Signal icon={<IconClock />} tone="warning" value={loadingValue(stats.impactNotices)} label="Changes awaiting impact review" />
      </section>
      {sampled && dataReady && <p className="footnote">Health, requirement, test and task figures cover your {SAMPLE_SIZE} most recently updated projects. Pick a project in the filter to see its full report.</p>}

      {dataReady && stats.pendingApprovals.length > 0 && (
        <section className="panel dash-callout" aria-labelledby="pending-title">
          <header className="panel-head tight">
            <span className="dash-callout-icon" aria-hidden="true">
              <IconLock width={16} height={16} />
            </span>
            <div className="dash-callout-text">
              <h2 id="pending-title" className="panel-title">
                Stage transitions awaiting Reviewer approval
              </h2>
              <p className="panel-sub">These projects stay in their current stage until a Reviewer who did not submit the request approves it.</p>
            </div>
            <span className="status-pill tone-warning">{stats.pendingApprovals.length} pending</span>
          </header>
          <ul className="pending-list">
            {stats.pendingApprovals.map(({ project, request }) => (
              <li key={request.id}>
                <Link to={`/projects/${project.id}#lifecycle`} className="pending-link">
                  <span className="pending-name">{project.name}</span>
                  <span className="pending-move">
                    {stageLabel(request.fromStatus)} → {stageLabel(request.toStatus)}
                  </span>
                  <span className="muted">
                    Submitted by {roleLabel(request.requestedByRole)} · {formatRelativeTime(request.requestedAt)}
                  </span>
                  <span className="status-pill tone-warning">Awaiting Reviewer</span>
                </Link>
              </li>
            ))}
          </ul>
        </section>
      )}

      {single && sp && sh ? (
        <div className="dash-main">
          <section className={`panel dash-project-report${sp.finished ? ' is-complete' : ''}`} aria-labelledby="report-title">
            <header className="panel-head">
              <div className="dash-report-title">
                <span className="dash-project-icon" aria-hidden="true">
                  <IconFolder width={16} height={16} />
                </span>
                <div>
                  <h2 id="report-title">{single.name}</h2>
                  <p>Updated {formatRelativeTime(single.updatedAt)}</p>
                </div>
              </div>
              <div className="dash-report-badges">
                <StagePill status={single.status} />
                <HealthBadge health={sh.health} reasons={[]} loading={!dataReady} />
              </div>
            </header>
            <div className="dash-report-progress">
              <div className="dash-report-progress-head">
                <span>Lifecycle progress</span>
                <strong>{sp.percent}%</strong>
              </div>
              <Meter percent={sp.percent} done={sp.finished} />
            </div>
            <div className="lc-stepper-wrap">
              <LifecycleStepper status={single.status} pending={!!sl?.pendingTransition} gaps={sl?.evidenceGaps.map((g) => g.stage)} to={`/projects/${single.id}#lifecycle`} />
            </div>
            <p className="dash-report-note">
              {sp.finished ? (
                <>
                  <IconCheck width={13} height={13} strokeWidth={3} aria-hidden="true" /> Lifecycle completed: all {LIFECYCLE_STAGES.length} stages approved.
                </>
              ) : sl?.pendingTransition ? (
                <>
                  Awaiting Reviewer approval to move from <strong>{stageLabel(sl.pendingTransition.fromStatus)}</strong> to <strong>{stageLabel(sl.pendingTransition.toStatus)}</strong>.
                </>
              ) : (
                <>
                  Currently in <strong>{stageLabel(single.status)}</strong>
                  {sp.stage < LAST_STAGE && (
                    <>
                      {' '}
                      · next: <strong>{stageLabel(single.status + 1)}</strong>
                    </>
                  )}
                  .
                </>
              )}
            </p>
          </section>

          <div className="dash-side">
            <section className="panel" aria-labelledby="health-title">
              <header className="panel-head tight">
                <div>
                  <h2 id="health-title" className="panel-title">
                    Project health
                  </h2>
                  <p className="panel-sub">Reviewer requests, evidence, conflicts and change impacts</p>
                </div>
              </header>
              {!dataReady ? (
                <>
                  <div className="skeleton skeleton-line" />
                  <div className="skeleton skeleton-line" />
                  <div className="skeleton skeleton-line" />
                </>
              ) : (
                <ul className="health-list">
                  <HealthRow label="Stage approval" value={sl?.pendingTransition ? 'Awaiting Reviewer' : 'None pending'} tone={sl?.pendingTransition ? 'warn' : 'good'} />
                  <HealthRow label="Evidence gaps" value={sl ? sl.evidenceGaps.length : '—'} tone={!sl ? 'neutral' : sl.evidenceGaps.length > 0 ? 'warn' : 'good'} />
                  <HealthRow label="Potential conflicts" value={sRow?.conflictCount ?? '—'} tone={sRow?.conflictCount == null ? 'neutral' : sRow.conflictCount > 0 ? 'warn' : 'good'} />
                  <HealthRow
                    label="Changes awaiting impact review"
                    value={sRow?.openImpactNoticeCount ?? '—'}
                    tone={sRow?.openImpactNoticeCount == null ? 'neutral' : sRow.openImpactNoticeCount > 0 ? 'warn' : 'good'}
                  />
                  <HealthRow label="Stakeholders" value={sRow?.stakeholderCount ?? '—'} tone="neutral" />
                </ul>
              )}
              <Link to={`/projects/${single.id}`} className="btn btn-ghost btn-sm dash-side-link">
                Open project <IconArrowRight width={14} height={14} />
              </Link>
            </section>
          </div>
        </div>
      ) : (
        <div className="dash-main">
          <section className="panel dash-projects" aria-labelledby="projects-title">
            <header className="panel-head">
              <div>
                <h2 id="projects-title">Projects</h2>
                <p>{recent.length < list.length ? `${recent.length} most recently updated of ${list.length}` : 'Sorted by most recent update'}</p>
              </div>
              <Link to="/projects" className="btn btn-ghost btn-sm">
                Manage projects <IconArrowRight width={14} height={14} />
              </Link>
            </header>
            <div className="table-scroll">
              <table className="dash-table">
                <thead>
                  <tr>
                    <th scope="col">Project</th>
                    <th scope="col">Stage</th>
                    <th scope="col">Lifecycle progress</th>
                    <th scope="col">Health</th>
                    <th scope="col" className="num dash-cell-time">
                      Updated
                    </th>
                    <th scope="col">
                      <span className="sr-only">Actions</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {recent.map((p) => {
                    const progress = lifecycleProgress(p.status);
                    const h = stats.health.get(p.id) ?? { health: 'unknown' as const, reasons: [] };
                    return (
                      <tr key={p.id} className={progress.finished ? 'is-complete' : undefined}>
                        <th scope="row">
                          <Link to={`/projects/${p.id}`} className="dash-project-link" title={p.name}>
                            <span className="dash-project-icon" aria-hidden="true">
                              <IconFolder width={15} height={15} />
                            </span>
                            <span className="dash-project-name">{p.name}</span>
                          </Link>
                        </th>
                        <td>
                          <StagePill status={p.status} />
                        </td>
                        <td>
                          <div className="dash-progress-cell" role="img" aria-label={`Lifecycle ${progress.percent}% complete, stage ${progress.stageNumber} of ${LIFECYCLE_STAGES.length}`}>
                            <LifecycleDots status={p.status} pending={!!stats.lifecycleById.get(p.id)?.pendingTransition} />
                            <span className="dash-pct">{progress.percent}%</span>
                          </div>
                          <span className="dash-cell-sub">{progress.finished ? `All ${LIFECYCLE_STAGES.length} stages complete` : `Stage ${progress.stageNumber} of ${LIFECYCLE_STAGES.length}`}</span>
                        </td>
                        <td>
                          <HealthBadge health={h.health} reasons={h.reasons} loading={!dataReady} />
                        </td>
                        <td className="num dash-cell-time">{formatRelativeTime(p.updatedAt)}</td>
                        <td className="num">
                          <button type="button" className="btn btn-ghost btn-sm dash-row-action" onClick={() => setScope(p.id)} aria-label={`View progress report for ${p.name}`}>
                            View report
                          </button>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
            <p className="footnote dash-table-note">Use “View report” or the filter above to see one project's progress on its own.</p>
          </section>

          <div className="dash-side">
            <section className="panel" aria-labelledby="health-title">
              <header className="panel-head tight">
                <div>
                  <h2 id="health-title" className="panel-title">
                    Portfolio health
                  </h2>
                  <p className="panel-sub">From Reviewer requests, evidence gaps, conflicts and change impacts</p>
                </div>
              </header>
              {!dataReady ? (
                <>
                  <div className="skeleton health-stack" />
                  <div className="skeleton skeleton-line" />
                  <div className="skeleton skeleton-line" />
                </>
              ) : (
                <>
                  <div className="health-stack" role="img" aria-label={shownHealth.map((h) => `${HEALTH_LABEL[h]}: ${stats.healthCounts[h]}`).join(', ')}>
                    {shownHealth
                      .filter((h) => stats.healthCounts[h] > 0)
                      .map((h) => (
                        <span key={h} className={`health-seg ${h}`} style={{ flexGrow: stats.healthCounts[h] }} />
                      ))}
                  </div>
                  <ul className="health-legend">
                    {shownHealth.map((h) => (
                      <li key={h}>
                        <span className={`health-swatch ${h}`} aria-hidden="true" />
                        <span className="health-legend-label">{HEALTH_LABEL[h]}</span>
                        <strong>{stats.healthCounts[h]}</strong>
                        <span className="health-legend-pct">{Math.round((stats.healthCounts[h] / list.length) * 100)}%</span>
                      </li>
                    ))}
                  </ul>
                </>
              )}
            </section>

            <section className="panel" aria-labelledby="dist-title">
              <header className="panel-head tight">
                <div>
                  <h2 id="dist-title" className="panel-title">
                    Projects by lifecycle stage
                  </h2>
                </div>
              </header>
              <DonutChart slices={stageSlices} centerValue={list.length} centerLabel="projects" label="Projects by lifecycle stage" />
            </section>
          </div>
        </div>
      )}

      {!single && (
        <section className="panel" aria-labelledby="matrix-title">
          <header className="panel-head">
            <div>
              <h2 id="matrix-title">Stage by project</h2>
              <p>
                Where every project is in its lifecycle · average <strong>{stats.avg}%</strong> · {stats.completed} of {list.length} completed
                {dataReady && stats.evidenceGaps > 0 && ` · ${stats.evidenceGaps} with an evidence gap`}
              </p>
            </div>
            <Link to="/projects" className="btn btn-ghost btn-sm">
              All projects <IconArrowRight width={14} height={14} />
            </Link>
          </header>
          <div className="table-scroll">
            <table className="sdlc-matrix dash-matrix">
              <thead>
                <tr>
                  <th scope="col">Project</th>
                  {LIFECYCLE_STAGES.map((s) => (
                    <th key={s.status} scope="col" title={s.description}>
                      <span className="matrix-step">{s.status + 1}</span>
                      <span className="matrix-stage">{s.label}</span>
                    </th>
                  ))}
                  <th scope="col" className="num">
                    Progress
                  </th>
                </tr>
              </thead>
              <tbody>
                {ranked.map(({ project, progress }) => {
                  const awaiting = !!stats.lifecycleById.get(project.id)?.pendingTransition;
                  return (
                    <tr key={project.id} className={progress.finished ? 'is-complete' : undefined}>
                      <th scope="row">
                        <Link to={`/projects/${project.id}`} title={project.name}>
                          {project.name}
                        </Link>
                      </th>
                      {LIFECYCLE_STAGES.map((s) => {
                        // The final stage, once reached, is finished rather than current (as in LifecycleStepper).
                        const reached = stageState(s.status, project.status);
                        const state = reached === 'current' && s.status === LAST_STAGE ? 'completed' : reached;
                        const text = state === 'current' && awaiting ? 'Awaiting review' : STAGE_STATE_LABEL[state];
                        return (
                          <td key={s.status}>
                            <span className={`matrix-cell ${state}${state === 'current' && awaiting ? ' awaiting' : ''}`} role="img" aria-label={`${s.label}: ${text}`} title={`${s.label}: ${text}`}>
                              {state === 'completed' ? <IconCheck width={12} height={12} strokeWidth={3} /> : state === 'current' ? <span className="dot" /> : null}
                            </span>
                          </td>
                        );
                      })}
                      <td className="num">
                        <span className="matrix-progress">
                          <Meter percent={progress.percent} done={progress.finished} />
                          <strong>{progress.percent}%</strong>
                        </span>
                      </td>
                    </tr>
                  );
                })}
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
              Current stage
            </li>
            <li>
              <span className="matrix-cell current awaiting">
                <span className="dot" />
              </span>
              Awaiting Reviewer approval
            </li>
            <li>
              <span className="matrix-cell upcoming" />
              Upcoming
            </li>
          </ul>
        </section>
      )}

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
                {stats.coveredFrs} of {stats.totalFrs} functional requirements have a test case
              </p>
            </div>
          </div>
          <div className="insight-card">
            <RingGauge percent={stats.lifecycleCompletion} color="var(--chart-c7)" label="Lifecycle completed" />
            <div>
              <h3>Lifecycle completed</h3>
              <p>{single ? (sp?.finished ? 'Approved as Completed' : 'Not yet approved as Completed') : `${stats.completed} of ${list.length} projects approved as Completed`}</p>
            </div>
          </div>
          <div className="insight-card">
            <RingGauge percent={stats.avg} color="var(--chart-c4)" label="Lifecycle progress" />
            <div>
              <h3>Lifecycle progress</h3>
              <p>
                {single ? 'Across' : 'Average across'} the 8 stages · {stats.stakeholders} stakeholders
              </p>
            </div>
          </div>
        </div>
      )}

      <div className="chart-grid">
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

        <section className="panel" aria-labelledby="type-title">
          <header className="panel-head tight">
            <div>
              <h2 id="type-title" className="panel-title">
                Artifacts by type
              </h2>
              <p className="panel-sub">{single ? 'Everything generated in this project' : 'Everything generated across your projects'}</p>
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

        <section className="panel" aria-labelledby="qa-title">
          <header className="panel-head tight">
            <div>
              <h2 id="qa-title" className="panel-title">
                Testing &amp; QA status
              </h2>
              <p className="panel-sub">
                {single
                  ? single.status === 6
                    ? 'This project is in the Testing stage'
                    : `This project is in ${stageLabel(single.status)}`
                  : `${stats.inTesting} ${stats.inTesting === 1 ? 'project is' : 'projects are'} in the Testing stage`}
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
      </div>

      {!single && (
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
      )}

      <section className="panel" aria-labelledby="activity-title">
        <header className="panel-head tight">
          <div>
            <h2 id="activity-title" className="panel-title">
              Recent activity
            </h2>
            <p className="panel-sub">{single ? `Latest artifact changes in ${single.name}` : 'Latest artifact changes across projects'}</p>
          </div>
        </header>
        {!dataReady ? (
          <div className="skeleton skeleton-panel" />
        ) : stats.activity.length === 0 ? (
          <EmptyState compact icon={<IconClock width={18} height={18} />} title="No activity yet" description="Changes to requirements, design, tasks and tests show up here." />
        ) : (
          <ul className="activity-list dash-activity">
            {stats.activity.map((a) => (
              <li key={a.id}>
                <Link to={`/projects/${a.projectId}`} className="activity-link">
                  <span className={`dash-activity-icon ${a.created ? 'created' : 'updated'}`} aria-hidden="true">
                    {a.created ? <IconPlus width={13} height={13} /> : <IconEdit width={13} height={13} />}
                  </span>
                  <span className="dash-activity-body">
                    <span className="activity-text">
                      <span className="dash-code">{a.code}</span> {a.title}
                    </span>
                    <span className="activity-meta">
                      {a.created ? 'Created' : 'Updated'} in {a.project} · {formatRelativeTime(a.at)}
                    </span>
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

/** Thin horizontal progress meter; turns the success colour once the lifecycle is finished. */
function Meter({ percent, done }: { percent: number; done?: boolean }) {
  return (
    <span className={`dash-meter${done ? ' done' : ''}`} aria-hidden="true">
      <span style={{ width: `${Math.min(Math.max(percent, 0), 100)}%` }} />
    </span>
  );
}

/** The project's lifecycle stage; Completed carries a check so it never reads as an open stage. */
function StagePill({ status }: { status: number }) {
  const finished = lifecycleProgress(status).finished;
  return (
    <span className={`status-pill tone-${statusVariant(status)}`}>
      {finished && <IconCheck width={11} height={11} strokeWidth={3} aria-hidden="true" />}
      {statusLabel(status)}
    </span>
  );
}

function HealthBadge({ health, reasons, loading }: { health: ProjectHealth; reasons: string[]; loading: boolean }) {
  if (loading && health === 'unknown') return <span className="health-badge loading">Checking…</span>;
  const detail = reasons.join(' · ');
  return (
    <span className="health-cell">
      <span className={`health-badge ${health}`} title={detail || undefined}>
        <span className="health-badge-dot" aria-hidden="true" />
        {HEALTH_LABEL[health]}
      </span>
      {reasons.length > 0 && (
        <span className="dash-cell-sub" title={detail}>
          {reasons[0]}
          {reasons.length > 1 && ` +${reasons.length - 1} more`}
        </span>
      )}
    </span>
  );
}

/** One row of the single-project health list (same look as the project overview's health panel). */
function HealthRow({ label, value, tone }: { label: string; value: number | string; tone: 'good' | 'warn' | 'bad' | 'neutral' }) {
  return (
    <li>
      <span className={`health-dot ${tone}`} aria-hidden="true" />
      <span className="health-label">{label}</span>
      <strong>{value}</strong>
    </li>
  );
}

function Signal({ icon, tone, value, label, meta }: { icon: ReactNode; tone: 'primary' | 'success' | 'warning' | 'danger'; value: number | string; label: string; meta?: string }) {
  return (
    <div className={`dash-signal tone-${tone}${value === 0 ? ' is-zero' : ''}`}>
      <span className="dash-signal-icon" aria-hidden="true">
        {icon}
      </span>
      <span className="dash-signal-text">
        <strong>{value}</strong>
        <span>{label}</span>
        {meta && <small>{meta}</small>}
      </span>
    </div>
  );
}
