import { Link } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { useProject } from '../context/ProjectContext';
import { useProjectInsights } from '../hooks/useProjectInsights';
import { deriveSdlc, phasePath } from '../lib/sdlc';
import { BUCKET_COLOR, BUCKET_LABEL, BUCKET_ORDER, countBuckets, isRequirement } from '../lib/requirements';
import { formatFullDateTime, formatRelativeTime, statusLabel, statusVariant } from '../lib/format';
import { PageHeader } from '../components/ui/PageHeader';
import { EmptyState } from '../components/ui/EmptyState';
import { Avatar } from '../components/ui/Avatar';
import { SdlcStepper } from '../components/SdlcStepper';
import { ProjectActionsMenu } from '../components/ProjectActionsMenu';
import { IconArrowRight, IconDocument, IconEdit, IconInbox, IconSparkles, IconUsers } from '../components/icons';

export function ProjectOverviewPage() {
  const { hasRole } = useAuth();
  const { project, setProject, contentVersion } = useProject();
  const insights = useProjectInsights(project.id, contentVersion);
  const sdlc = deriveSdlc(project.status);
  const canEdit = hasRole('Administrator') || hasRole('BusinessAnalyst');

  const { dashboard, artifacts, stakeholders, matrix, loading } = insights;
  const requirements = artifacts?.filter(isRequirement) ?? null;
  const buckets = requirements ? countBuckets(requirements) : null;

  // The single most useful next step, chosen only from real counts.
  const next = (() => {
    const phaseTarget = sdlc.current ? phasePath(project.id, sdlc.current.segment) : phasePath(project.id, 'traceability');
    if (requirements && requirements.length === 0)
      return { title: 'Capture your first requirements', text: 'Add stakeholder notes or a document and let AI-REAP structure them.', to: phasePath(project.id, 'gathering'), cta: 'Start Requirements Gathering' };
    if (dashboard && dashboard.openClarificationCount > 0)
      return { title: `Resolve ${dashboard.openClarificationCount} open clarification ${dashboard.openClarificationCount === 1 ? 'question' : 'questions'}`, text: 'Unanswered questions leave requirements ambiguous.', to: phasePath(project.id, 'validation'), cta: 'Open Validation' };
    if (dashboard && dashboard.pendingReviewCount > 0)
      return { title: `Review ${dashboard.pendingReviewCount} pending ${dashboard.pendingReviewCount === 1 ? 'artifact' : 'artifacts'}`, text: 'Approve or reject AI-generated work before moving on.', to: phasePath(project.id, 'analysis'), cta: 'Review requirements' };
    return sdlc.finished
      ? { title: 'This project has completed every SDLC phase', text: 'Review end-to-end coverage in the traceability matrix.', to: phasePath(project.id, 'traceability'), cta: 'Open Traceability' }
      : { title: `Continue with ${sdlc.current?.navLabel ?? 'the next phase'}`, text: sdlc.current?.description ?? '', to: phaseTarget, cta: `Open ${sdlc.current?.label ?? 'phase'}` };
  })();

  const rowsWith = (pick: (r: NonNullable<typeof matrix>[number]) => number) => (matrix && matrix.length > 0 ? matrix.filter((r) => pick(r) > 0).length : null);
  const withTests = rowsWith((r) => r.testCases.length);
  const withDesign = rowsWith((r) => r.designArtifacts.length);

  return (
    <>
      <PageHeader
        title={project.name}
        subtitle={project.description || 'No description yet.'}
        badge={<span className={`status-pill tone-${statusVariant(project.status)}`}>{statusLabel(project.status)}</span>}
        actions={
          <>
            {canEdit && (
              <Link to={`/projects/${project.id}/edit`} className="btn btn-secondary">
                <IconEdit width={15} height={15} /> Edit Project
              </Link>
            )}
            <ProjectActionsMenu project={project} onUpdated={setProject} showOpen={false} label="More project actions" />
          </>
        }
      />

      <section className="panel sdlc-panel" aria-labelledby="sdlc-title">
        <header className="panel-head">
          <div>
            <h2 id="sdlc-title">SDLC progress</h2>
            <p>
              {sdlc.finished ? 'Every phase is complete.' : `Currently in ${sdlc.current?.navLabel}.`} {sdlc.completed} of {sdlc.phases.length} phases complete.
            </p>
          </div>
          <div className="big-percent" aria-label={`${sdlc.percent}% complete`}>
            {sdlc.percent}
            <span>%</span>
          </div>
        </header>
        <div className="progress-track progress-lg" aria-hidden="true">
          <div className="progress-fill" style={{ width: `${Math.max(sdlc.percent, 2)}%` }} />
        </div>
        <SdlcStepper phases={sdlc.phases} projectId={project.id} />
      </section>

      <div className="overview-grid">
        <div className="overview-main">
          <div className="next-action">
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
                <dt>Status</dt>
                <dd>{statusLabel(project.status)}</dd>
              </div>
              <div>
                <dt>Current phase</dt>
                <dd>{sdlc.current?.navLabel ?? 'All phases complete'}</dd>
              </div>
              <div>
                <dt>Progress</dt>
                <dd>{sdlc.percent}%</dd>
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
                    <HealthRow label="Awaiting review" value={dashboard.pendingReviewCount} tone={dashboard.pendingReviewCount === 0 ? 'good' : 'warn'} />
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
