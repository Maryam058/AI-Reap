import { useCallback, useEffect } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { projectsApi } from '../api/projects';
import { useProject } from '../context/ProjectContext';
import { useProjectInsights } from '../hooks/useProjectInsights';
import { isGatedStage, lifecycleProgress, LIFECYCLE_STAGES, stageLabel } from '../lib/lifecycle';
import { phasePath, workAreaForStage } from '../lib/workAreas';
import { BUCKET_COLOR, BUCKET_LABEL, BUCKET_ORDER, countBuckets, isRequirement } from '../lib/requirements';
import { formatFullDateTime, formatRelativeTime, initials, statusLabel, statusVariant } from '../lib/format';
import { activeStagesLabel, completedWorkStages, currentStage, STAGE_COUNT } from '../lib/stages';
import { useStageSignals } from '../context/StageSignalsContext';
import { ProgressBar } from '../components/ui/ProgressBar';
import { Term } from '../components/ui/Term';
import { ProjectJourney } from '../components/stage/ProjectJourney';
import { EmptyState } from '../components/ui/EmptyState';
import { ExpandableText } from '../components/ui/ExpandableText';
import { Avatar } from '../components/ui/Avatar';
import { ProjectActionsMenu } from '../components/ProjectActionsMenu';
import { LifecyclePanel } from '../components/LifecyclePanel';
import { AgentActivitySummary } from '../components/AgentActivitySummary';
import { ProjectTeamCard } from '../components/ProjectTeamCard';
import { IconArrowRight, IconCheckCircle, IconDocument, IconEdit, IconInbox, IconSparkles, IconUsers } from '../components/icons';

export function ProjectOverviewPage() {
  const { hasRole, token } = useAuth();
  const { project, setProject, contentVersion } = useProject();
  const insights = useProjectInsights(project.id, contentVersion);
  const progress = lifecycleProgress(project.status);
  const stageArea = workAreaForStage(project.status);
  const canEdit = hasRole('Administrator') || hasRole('BusinessAnalyst');
  const { hash } = useLocation();
  const signals = useStageSignals();
  const focus = currentStage(project.status);
  const work = completedWorkStages(project.status);

  // A lifecycle change moves Project.status on the server; re-read the project so the whole shell follows.
  const refreshProject = useCallback(async () => {
    try {
      setProject(await projectsApi.get(project.id, token));
    } catch {
      // The lifecycle panel already shows the outcome; the header catches up on the next load.
    }
  }, [project.id, token, setProject]);

  // Notifications link to #lifecycle.
  useEffect(() => {
    if (hash === '#lifecycle') document.getElementById('lifecycle')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }, [hash]);

  const { dashboard, artifacts, stakeholders, matrix, loading } = insights;
  const requirements = artifacts?.filter(isRequirement) ?? null;
  const buckets = requirements ? countBuckets(requirements) : null;

  // The single most useful next step, chosen only from real counts.
  const next = (() => {
    const stageTarget = phasePath(project.id, stageArea.segment);
    // A Completed project has no lifecycle move left, so leftover review items (e.g. answered
    // clarification questions not yet resolved, draft objectives) are information, not a next step.
    if (progress.finished) {
      const leftover = dashboard?.pendingReviewCount ?? 0;
      return {
        title: 'This project has completed its lifecycle',
        text:
          leftover > 0
            ? `Review what each stage produced, who approved it and how it is traced. ${leftover} artifact ${leftover === 1 ? 'item still requires' : 'items still require'} review; ${leftover === 1 ? 'it is' : 'they are'} not needed for the completed lifecycle.`
            : 'Review what each stage produced, who approved it and how it is traced.',
        to: phasePath(project.id, 'review'),
        cta: 'Open SDLC Review',
      };
    }
    if (requirements && requirements.length === 0)
      return { title: 'Capture your first requirements', text: 'Add stakeholder notes or a document and let AI-REAP structure them.', to: phasePath(project.id, 'gathering'), cta: 'Start Requirements Gathering' };
    if (dashboard && dashboard.openClarificationCount > 0)
      return { title: `Resolve ${dashboard.openClarificationCount} open clarification ${dashboard.openClarificationCount === 1 ? 'question' : 'questions'}`, text: 'Unanswered questions leave requirements ambiguous.', to: phasePath(project.id, 'validation'), cta: 'Open Validation' };
    if (dashboard && dashboard.pendingReviewCount > 0)
      return { title: `Review ${dashboard.pendingReviewCount} pending ${dashboard.pendingReviewCount === 1 ? 'artifact' : 'artifacts'}`, text: 'Approve or reject AI-generated work before moving on.', to: phasePath(project.id, 'analysis'), cta: 'Review requirements' };
    return { title: `Continue the ${stageLabel(project.status)} work`, text: stageArea.description, to: stageTarget, cta: `Open ${stageArea.label}` };
  })();

  const rowsWith = (pick: (r: NonNullable<typeof matrix>[number]) => number) => (matrix && matrix.length > 0 ? matrix.filter((r) => pick(r) > 0).length : null);
  const withTests = rowsWith((r) => r.testCases.length);
  const withDesign = rowsWith((r) => r.designArtifacts.length);
  // §28 project-wide dashboard aggregates (not visibility-filtered, unlike the matrix rows above).
  const needingReview = dashboard?.requirementsNeedingReviewCount ?? 0;
  const testedFrs = dashboard?.testedFunctionalRequirementCount ?? 0;

  return (
    <>
      <section className="project-hero" aria-labelledby="project-title">
        <div className="project-hero-main">
          <span className="project-hero-avatar" aria-hidden="true">
            {initials(project.name)}
          </span>
          <div className="project-hero-text">
            <span className="eyebrow">Project</span>
            <h1 id="project-title">{project.name}</h1>
            <div className="project-hero-desc">{project.description ? <ExpandableText lines={2}>{project.description}</ExpandableText> : <span className="muted">No description yet.</span>}</div>
            {(project.domain || project.targetUsers || project.expectedTimeline) && (
              <dl className="project-hero-facts">
                {project.domain && (
                  <div>
                    <dt>Domain</dt>
                    <dd>{project.domain}</dd>
                  </div>
                )}
                {project.targetUsers && (
                  <div>
                    <dt>Target users</dt>
                    <dd>{project.targetUsers}</dd>
                  </div>
                )}
                {project.expectedTimeline && (
                  <div>
                    <dt>Timeline</dt>
                    <dd>{project.expectedTimeline}</dd>
                  </div>
                )}
                <div>
                  <dt>Updated</dt>
                  <dd title={formatFullDateTime(project.updatedAt)}>{formatRelativeTime(project.updatedAt)}</dd>
                </div>
              </dl>
            )}
          </div>
          <div className="page-header-actions project-hero-actions">
            <Link to={phasePath(project.id, 'review')} className={`btn ${progress.finished ? 'btn-primary' : 'btn-secondary'}`}>
              <IconCheckCircle width={15} height={15} /> SDLC Review
            </Link>
            {canEdit && (
              <Link to={`/projects/${project.id}/edit`} className="btn btn-secondary">
                <IconEdit width={15} height={15} /> Edit Project
              </Link>
            )}
            <ProjectActionsMenu project={project} onUpdated={setProject} showOpen={false} label="More project actions" />
          </div>
        </div>
        <div className="project-hero-stats">
          <div className="hero-stat hero-stat-progress">
            <span className="hero-stat-label">Overall progress</span>
            <strong className="hero-stat-value">{progress.percent}%</strong>
            <ProgressBar percent={progress.percent} />
            <small>Based on the approved lifecycle status</small>
          </div>
          <div className="hero-stat">
            <span className="hero-stat-label">Current stage</span>
            <strong className="hero-stat-value text">{activeStagesLabel(project.status)}</strong>
            <small>
              Stage {focus.number} of {STAGE_COUNT}
            </small>
          </div>
          <div className="hero-stat">
            <span className="hero-stat-label">
              <Term id="lifecycleStatus" />
            </span>
            <span className={`status-pill tone-${statusVariant(project.status)}`}>{statusLabel(project.status)}</span>
            <small>
              {progress.finished ? (
                'Lifecycle completed'
              ) : (
                <>
                  Next: {stageLabel(project.status + 1)}
                  {isGatedStage(project.status) && ' (needs Reviewer approval)'}
                </>
              )}
            </small>
          </div>
          <div className="hero-stat">
            <span className="hero-stat-label">Work stages completed</span>
            <strong className="hero-stat-value">
              {work.done}
              <span className="hero-stat-of"> / {work.total}</span>
            </strong>
            <small>AI Copilot and Review are available anytime</small>
          </div>
        </div>
      </section>

      <div className="next-action next-action-hero">
        <span className="next-action-icon">
          <IconSparkles width={18} height={18} />
        </span>
        <div className="next-action-text">
          <span className="eyebrow">Recommended next step</span>
          <h3>{next.title}</h3>
          <p>{next.text}</p>
        </div>
        <Link to={next.to} className="btn btn-primary">
          {next.cta} <IconArrowRight width={15} height={15} />
        </Link>
      </div>

      <ProjectJourney projectId={project.id} status={project.status} signals={signals} data={{ dashboard, artifacts, stakeholders, matrix }} />

      <LifecyclePanel project={project} version={contentVersion} onProjectChanged={refreshProject} />

      <AgentActivitySummary projectId={project.id} version={`${contentVersion}|${project.status}`} />

      <div className="overview-grid">
        <div className="overview-main">
          <section className="panel" aria-labelledby="req-title">
            <header className="panel-head">
              <div>
                <h2 id="req-title">Requirements</h2>
                <p>Functional and non-functional requirements across all sources.</p>
              </div>
              <Link to={phasePath(project.id, 'analysis')} className="btn btn-ghost btn-sm">
                View all
              </Link>
            </header>
            {loading && !requirements ? (
              <div className="skeleton skeleton-panel" />
            ) : !requirements || !buckets ? (
              <p className="muted">Requirement data is unavailable right now.</p>
            ) : requirements.length === 0 ? (
              <EmptyState
                compact
                icon={<IconDocument width={18} height={18} />}
                title="No requirements yet"
                description="Start by capturing the business needs and requirements for this project."
                action={
                  <Link to={phasePath(project.id, 'gathering')} className="btn btn-primary">
                    Start Requirements Gathering
                  </Link>
                }
              />
            ) : (
              <>
                <div className="req-total">
                  <strong>{requirements.length}</strong>
                  <span>total requirements</span>
                </div>
                <div className="stack-bar" role="img" aria-label={BUCKET_ORDER.map((b) => `${BUCKET_LABEL[b]} ${buckets[b]}`).join(', ')}>
                  {BUCKET_ORDER.filter((b) => buckets[b] > 0).map((b) => (
                    <span key={b} style={{ flexGrow: buckets[b], background: BUCKET_COLOR[b] }} title={`${BUCKET_LABEL[b]}: ${buckets[b]}`} />
                  ))}
                </div>
                <ul className="chart-legend inline">
                  {BUCKET_ORDER.map((b) => (
                    <li key={b}>
                      <span className="chart-swatch" style={{ background: BUCKET_COLOR[b] }} aria-hidden="true" />
                      <span className="chart-legend-label">{BUCKET_LABEL[b]}</span>
                      <strong>{buckets[b]}</strong>
                    </li>
                  ))}
                </ul>
              </>
            )}
          </section>

          <section className="panel" aria-labelledby="activity-title">
            <header className="panel-head">
              <div>
                <h2 id="activity-title">Recent activity</h2>
                <p>Latest changes to requirements and generated artifacts.</p>
              </div>
            </header>
            {loading && !dashboard ? (
              <div className="skeleton skeleton-panel" />
            ) : !dashboard ? (
              <p className="muted">Activity is unavailable right now.</p>
            ) : dashboard.recentChanges.length === 0 ? (
              <EmptyState compact icon={<IconInbox width={18} height={18} />} title="No activity yet" description="Changes to requirements and AI-generated artifacts will show up here." />
            ) : (
              <ul className="activity-list">
                {dashboard.recentChanges.map((c, i) => (
                  <li key={`${c.code}-${i}`} className="activity-item">
                    <span className="activity-dot">
                      <IconSparkles />
                    </span>
                    <div className="activity-body">
                      <div className="activity-title">
                        <span className="code">{c.code}</span>
                        {c.title}
                      </div>
                      <div className="activity-meta">
                        {c.artifactType} · {c.status} · {formatRelativeTime(c.updatedAt)}
                      </div>
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </div>

        <aside className="overview-side">
          <section className="panel" aria-labelledby="details-title">
            <h2 id="details-title" className="panel-title">
              Project details
            </h2>
            <dl className="kv">
              <div>
                <dt>Lifecycle stage</dt>
                <dd>
                  {statusLabel(project.status)} ({progress.stageNumber} of {LIFECYCLE_STAGES.length})
                </dd>
              </div>
              <div>
                <dt>Next stage</dt>
                <dd>{progress.finished ? 'None - completed' : stageLabel(project.status + 1)}</dd>
              </div>
              <div>
                <dt>Lifecycle progress</dt>
                <dd>{progress.percent}%</dd>
              </div>
              <div>
                <dt>Last updated</dt>
                <dd title={formatFullDateTime(project.updatedAt)}>{formatRelativeTime(project.updatedAt)}</dd>
              </div>
              <div>
                <dt>Created</dt>
                <dd>{formatFullDateTime(project.createdAt)}</dd>
              </div>
            </dl>
          </section>

          <ProjectTeamCard projectId={project.id} />

          <section className="panel" aria-labelledby="sh-title">
            <header className="panel-head tight">
              <h2 id="sh-title" className="panel-title">
                Stakeholders
              </h2>
              <Link to={`${phasePath(project.id, 'gathering')}?tab=stakeholders`} className="btn btn-ghost btn-sm">
                Manage
              </Link>
            </header>
            {loading && !stakeholders ? (
              <div className="skeleton skeleton-line" />
            ) : !stakeholders ? (
              <p className="muted">Stakeholders are unavailable right now.</p>
            ) : stakeholders.length === 0 ? (
              <p className="muted">
                <IconUsers width={14} height={14} /> No stakeholders recorded yet.
              </p>
            ) : (
              <>
                <div className="kv-big">
                  <strong>{stakeholders.length}</strong> {stakeholders.length === 1 ? 'stakeholder' : 'stakeholders'}
                </div>
                <ul className="mini-people">
                  {stakeholders.slice(0, 4).map((s) => (
                    <li key={s.id}>
                      <Avatar name={s.name} />
                      <span>
                        {s.name}
                        {s.roleInProject && <small>{s.roleInProject}</small>}
                      </span>
                    </li>
                  ))}
                </ul>
              </>
            )}
            {canEdit && (
              <p className="field-hint">
                Stakeholders don't get project access. To give a user access, add them under{' '}
                <Link to={`${phasePath(project.id, 'gathering')}?tab=members`}>Members</Link>.
              </p>
            )}
          </section>

          <section className="panel" aria-labelledby="health-title">
            <h2 id="health-title" className="panel-title">
              Project health
            </h2>
            {loading && !dashboard && !matrix ? (
              <div className="skeleton skeleton-line" />
            ) : !dashboard && !matrix ? (
              <p className="muted">Health data is unavailable right now.</p>
            ) : (
              <ul className="health-list">
                {dashboard && (
                  <>
                    <HealthRow label="Open clarifications" value={dashboard.openClarificationCount} tone={dashboard.openClarificationCount === 0 ? 'good' : 'warn'} />
                    <HealthRow label="Artifacts awaiting review" value={dashboard.pendingReviewCount} tone={dashboard.pendingReviewCount === 0 ? 'good' : 'warn'} />
                    <HealthRow label="Requirements needing review" value={needingReview} tone={needingReview === 0 ? 'good' : 'warn'} />
                    {dashboard.functionalRequirementCount > 0 && (
                      <HealthRow
                        label="Test coverage (FRs)"
                        value={`${testedFrs}/${dashboard.functionalRequirementCount} (${Math.round((testedFrs / dashboard.functionalRequirementCount) * 100)}%)`}
                        tone={testedFrs === dashboard.functionalRequirementCount ? 'good' : 'neutral'}
                      />
                    )}
                    <HealthRow label="Rejected" value={dashboard.rejectedCount} tone={dashboard.rejectedCount === 0 ? 'good' : 'bad'} />
                    <HealthRow label="Potential conflicts" value={dashboard.conflictCount} tone={dashboard.conflictCount === 0 ? 'good' : 'warn'} />
                  </>
                )}
                {matrix && matrix.length > 0 && withDesign !== null && withTests !== null && (
                  <>
                    <HealthRow label="Requirements with design" value={`${withDesign}/${matrix.length}`} tone={withDesign === matrix.length ? 'good' : 'neutral'} />
                    <HealthRow label="Requirements with tests" value={`${withTests}/${matrix.length}`} tone={withTests === matrix.length ? 'good' : 'neutral'} />
                  </>
                )}
              </ul>
            )}
          </section>
        </aside>
      </div>
    </>
  );
}

function HealthRow({ label, value, tone }: { label: string; value: number | string; tone: 'good' | 'warn' | 'bad' | 'neutral' }) {
  return (
    <li>
      <span className={`health-dot ${tone}`} aria-hidden="true" />
      <span className="health-label">{label}</span>
      <strong>{value}</strong>
    </li>
  );
}
