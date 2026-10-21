import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { useProject } from '../context/ProjectContext';
import { lifecycleReviewApi } from '../api/lifecycleReview';
import {
  ARTIFACT_STATUS_LABELS,
  RELATIONSHIP_TYPE_LABELS,
  REVIEW_DECISION,
  STAGE_REVIEW_STATE,
  TRANSITION_STATE,
  type LifecycleReview,
  type ReviewedArtifact,
  type ReviewTimelineEvent,
  type StageReviewSummary,
  type StageTransition,
} from '../api/types';
import { personLabel, useMemberNames } from '../hooks/useMemberNames';
import { LIFECYCLE_STAGES, roleLabel, stageLabel, submissionEvidenceNote } from '../lib/lifecycle';
import {
  artifactsById,
  artifactsCreatedIn,
  artifactTypeName,
  filterTimeline,
  formatDateTime,
  formatDuration,
  groupByDay,
  humanize,
  NOT_AVAILABLE,
  STAGE_UNAVAILABLE,
  stageOrUnavailable,
  WORK_OUTCOME_LABEL,
  type TimelineFilter,
} from '../lib/lifecycleReview';
import { phasePath } from '../lib/workAreas';
import { apiErrorText } from '../lib/errors';
import { PageHeader } from '../components/ui/PageHeader';
import { MetricCard } from '../components/ui/MetricCard';
import { Tabs } from '../components/ui/Tabs';
import { Dialog } from '../components/ui/Dialog';
import { ErrorState } from '../components/ui/ErrorState';
import { EmptyState } from '../components/ui/EmptyState';
import { PanelSkeleton } from '../components/ui/Skeletons';
import { LifecycleStepper } from '../components/LifecycleStepper';
import { ArtifactDetails } from '../components/ArtifactDetails';
import { TraceabilityMatrix } from '../components/TraceabilityMatrix';
import {
  IconArrowLeft,
  IconCheck,
  IconCheckCircle,
  IconClock,
  IconCode,
  IconDocument,
  IconLayers,
  IconLink,
  IconShieldCheck,
  IconSparkles,
  IconTarget,
} from '../components/icons';

type Tab = 'stages' | 'timeline' | 'traceability';
/** A lifecycle stage, or artifacts whose stage can't be determined. */
type StageKey = number | 'none';

const STATE_PILL: Record<number, { label: string; tone: string }> = {
  [STAGE_REVIEW_STATE.Completed]: { label: 'Completed', tone: 'success' },
  [STAGE_REVIEW_STATE.Current]: { label: 'Current stage', tone: 'info' },
  [STAGE_REVIEW_STATE.NotReached]: { label: 'Not reached', tone: 'neutral' },
};

const TRANSITION_STATE_LABEL: Record<number, string> = {
  [TRANSITION_STATE.Pending]: 'Pending',
  [TRANSITION_STATE.Completed]: 'Approved',
  [TRANSITION_STATE.Rejected]: 'Rejected',
  [TRANSITION_STATE.Withdrawn]: 'Withdrawn',
};

/** Where each artifact type is worked on, for "open in work area". */
function workSegment(type: number): string {
  if (type === 11) return 'gathering';
  if (type === 6 || type === 7 || type === 8) return 'design';
  if (type === 9) return 'development';
  if (type === 10) return 'testing';
  return 'analysis';
}

function countTypes(review: LifecycleReview, ...types: number[]): number {
  return review.totals.byType.filter((t) => types.includes(t.artifactType)).reduce((n, t) => n + t.count, 0);
}

/**
 * SDLC Review: a read-only, historical walk through the project's lifecycle. Every number and
 * stage attribution comes from GET /api/projects/{id}/lifecycle-review; artifacts open the
 * existing artifact details (versions, reviews, relationships, impact) rather than a new viewer.
 */
export function LifecycleReviewPage() {
  const { token, userId } = useAuth();
  const { project } = useProject();
  const names = useMemberNames(project.id);
  const [params, setParams] = useSearchParams();
  const [review, setReview] = useState<LifecycleReview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [openArtifact, setOpenArtifact] = useState<string | null>(null);

  const tab = (['stages', 'timeline', 'traceability'].includes(params.get('tab') ?? '') ? params.get('tab') : 'stages') as Tab;
  const stageParam = params.get('stage');
  const selected: StageKey = stageParam === 'none' ? 'none' : stageParam != null && !Number.isNaN(Number(stageParam)) ? Number(stageParam) : 1;

  const setParam = (key: string, value: string) =>
    setParams(
      (p) => {
        const next = new URLSearchParams(p);
        next.set(key, value);
        return next;
      },
      { replace: true },
    );

  const load = useCallback(async () => {
    try {
      setReview(await lifecycleReviewApi.get(project.id, token));
    } catch (err) {
      setError(apiErrorText(err, 'Could not load the lifecycle review.'));
    }
  }, [project.id, token]);

  useEffect(() => {
    void load();
  }, [load]);

  const who = useCallback((id: string | null | undefined, role?: string | null) => (id ? personLabel(id, role ?? null, names, userId) : NOT_AVAILABLE), [names, userId]);

  if (error) return <ErrorState title="Unable to load the SDLC review" description={error} onRetry={() => { setError(null); void load(); }} />;
  if (!review) {
    return (
      <div aria-busy="true" aria-label="Loading SDLC review">
        <div className="skeleton skeleton-title" />
        <PanelSkeleton />
        <PanelSkeleton />
      </div>
    );
  }

  const t = review.totals;
  const designCount = countTypes(review, 6, 7, 8);
  const testCount = countTypes(review, 10);
  const taskCount = countTypes(review, 9);

  return (
    <div className="sdlc-review">
      <PageHeader
        eyebrow="SDLC Review"
        title={review.projectName}
        subtitle="Stage by stage: what each lifecycle stage produced, who produced it, and how it was approved, implemented and verified. Read-only."
        badge={
          review.isCompleted ? (
            <span className="status-pill tone-success">
              <IconCheck width={12} height={12} /> Completed {review.completedAt ? formatDateTime(review.completedAt) : ''}
            </span>
          ) : (
            <span className="status-pill tone-info">In progress: {stageLabel(review.currentStatus)}</span>
          )
        }
        actions={
          <Link to={`/projects/${project.id}`} className="btn btn-secondary">
            <IconArrowLeft width={15} height={15} /> Project overview
          </Link>
        }
      />

      {!review.historyComplete && (
        <div className="alert alert-info" role="note">
          This project existed before lifecycle tracking was introduced, so its early history was not recorded. Stages and artifacts from before then are
          marked “{STAGE_UNAVAILABLE}” instead of being guessed.
        </div>
      )}

      <section className="panel" aria-labelledby="sdlc-summary-title">
        <header className="panel-head">
          <div>
            <h2 id="sdlc-summary-title">{review.isCompleted ? 'SDLC completion summary' : 'Lifecycle so far'}</h2>
            <p>
              {review.isCompleted
                ? 'Every lifecycle stage was passed and the delivery was approved. Select a stage below to review its artifacts and decisions.'
                : 'Stages the project has passed so far. Select a stage below to review its artifacts and decisions.'}
            </p>
          </div>
        </header>
        <LifecycleStepper status={review.currentStatus} />
        <div className="metric-grid rv-metrics">
          <MetricCard icon={<IconLayers width={18} height={18} />} tone="primary" value={t.totalArtifacts} label="Total artifacts" hint={`${t.aiGenerated} AI-generated · ${t.manual} manual`} />
          <MetricCard icon={<IconDocument width={18} height={18} />} tone="primary" value={countTypes(review, 0, 1)} label="Requirements" hint={`${countTypes(review, 0)} functional · ${countTypes(review, 1)} non-functional · ${countTypes(review, 2)} business rules`} />
          <MetricCard icon={<IconTarget width={18} height={18} />} tone="primary" value={countTypes(review, 3)} label="User stories" hint={`${countTypes(review, 4)} acceptance criteria`} />
          <MetricCard icon={<IconLayers width={18} height={18} />} tone="ai" value={designCount} label="Design artifacts" hint={`${countTypes(review, 6)} designs · ${countTypes(review, 7)} APIs · ${countTypes(review, 8)} data entities`} />
          <MetricCard icon={<IconCode width={18} height={18} />} tone="warning" value={taskCount} label="Implementation tasks" hint={`${t.tasksCompleted} completed`} />
          <MetricCard
            icon={<IconShieldCheck width={18} height={18} />}
            tone="success"
            value={testCount}
            label="Test cases"
            hint={`${t.testsPassed} passed · ${t.testsFailed} failed${t.testsBlocked ? ` · ${t.testsBlocked} blocked` : ''}`}
          />
          <MetricCard
            icon={<IconLink width={18} height={18} />}
            tone="success"
            value={t.traceabilityCoveragePercent == null ? NOT_AVAILABLE : `${t.traceabilityCoveragePercent}%`}
            label="Traceability coverage"
            hint={`${t.fullyTracedFunctionalRequirements} of ${t.functionalRequirements} requirements traced to story, criteria, task and test`}
          />
          <MetricCard icon={<IconSparkles width={18} height={18} />} tone="ai" value={t.aiOperations} label="AI operations" hint={`${t.agentRuns} agent runs · ${t.reviews} human reviews · ${t.versions} versions`} />
        </div>
      </section>

      <Tabs
        label="SDLC review views"
        active={tab}
        onChange={(k) => setParam('tab', k)}
        items={[
          { key: 'stages', label: 'Stages', count: review.stages.filter((s) => s.state !== STAGE_REVIEW_STATE.NotReached).length },
          { key: 'timeline', label: 'Timeline', count: review.timeline.length },
          { key: 'traceability', label: 'Traceability', count: review.traceability.length },
        ]}
      />

      <div role="tabpanel" id={`panel-${tab}`} aria-labelledby={`tab-${tab}`}>
        {tab === 'stages' && (
          <div className="rv-stages">
            <nav className="rv-stage-list" aria-label="Lifecycle stages">
              {review.stages.map((s) => (
                <StageCard key={s.stage} stage={s} active={selected === s.stage} onSelect={() => setParam('stage', String(s.stage))} />
              ))}
              {t.unattributedArtifacts > 0 && (
                <button type="button" className={`rv-stage-card unattributed${selected === 'none' ? ' active' : ''}`} onClick={() => setParam('stage', 'none')} aria-pressed={selected === 'none'}>
                  <span className="rv-stage-num">?</span>
                  <span className="rv-stage-text">
                    <span className="rv-stage-name">{STAGE_UNAVAILABLE}</span>
                    <span className="rv-stage-meta">{t.unattributedArtifacts} artifacts created before recorded history</span>
                  </span>
                </button>
              )}
            </nav>
            <div className="rv-stage-detail">
              {selected === 'none' ? (
                <UnattributedDetail review={review} who={who} onOpen={setOpenArtifact} />
              ) : (
                <StageDetail key={selected} review={review} stage={review.stages.find((s) => s.stage === selected) ?? review.stages[0]} who={who} onOpen={setOpenArtifact} />
              )}
            </div>
          </div>
        )}

        {tab === 'timeline' && <Timeline review={review} who={who} onOpen={setOpenArtifact} />}

        {tab === 'traceability' && (
          <>
            <p className="muted rv-trace-note">
              How requirements flowed into stories, acceptance criteria, design, implementation tasks and test cases. Select any artifact in the Stages tab to see its own
              links.
            </p>
            <TraceabilityMatrix projectId={project.id} />
          </>
        )}
      </div>

      {openArtifact && (
        <ArtifactProvenanceDialog
          review={review}
          artifactId={openArtifact}
          projectId={project.id}
          who={who}
          onOpen={setOpenArtifact}
          onClose={() => setOpenArtifact(null)}
        />
      )}
    </div>
  );
}

type Who = (id: string | null | undefined, role?: string | null) => string;

function StatePill({ state }: { state: number }) {
  const s = STATE_PILL[state] ?? STATE_PILL[STAGE_REVIEW_STATE.NotReached];
  return <span className={`status-pill tone-${s.tone}`}>{s.label}</span>;
}

function StageCard({ stage, active, onSelect }: { stage: StageReviewSummary; active: boolean; onSelect: () => void }) {
  const a = stage.activity;
  const done = stage.state === STAGE_REVIEW_STATE.Completed;
  return (
    <button type="button" className={`rv-stage-card${active ? ' active' : ''}${done ? ' done' : ''}`} onClick={onSelect} aria-pressed={active}>
      <span className="rv-stage-num" aria-hidden="true">
        {done ? <IconCheck width={13} height={13} strokeWidth={2.5} /> : stage.stage + 1}
      </span>
      <span className="rv-stage-text">
        <span className="rv-stage-name">{stage.label}</span>
        <span className="rv-stage-meta">
          {roleLabel(stage.ownerRole)}
          {stage.completedAt && ` · ${formatDateTime(stage.completedAt)}`}
        </span>
        <span className="rv-stage-counts">
          {stage.historyUnavailable ? (
            'History not recorded'
          ) : (
            <>
              {a.artifactsCreated} created · {a.approvals} approvals · {a.aiOperations} AI ops
              {stage.visits.length > 1 && ` · ${stage.visits.length} visits`}
            </>
          )}
        </span>
      </span>
    </button>
  );
}

function allTransitions(review: LifecycleReview): StageTransition[] {
  const byId = new Map<string, StageTransition>();
  review.stages.forEach((s) => s.transitions.forEach((t) => byId.set(t.id, t)));
  return [...byId.values()].sort((a, b) => a.sequence - b.sequence);
}

function TransitionRow({ t, who }: { t: StageTransition; who: Who }) {
  const evidenceNote = submissionEvidenceNote(t);
  const from = t.fromStatus == null ? null : stageLabel(t.fromStatus);
  const to = stageLabel(t.toStatus);
  const gated = !!t.decidedByUserId && t.decidedByUserId !== t.requestedByUserId;
  const title =
    t.kind === 0 ? `Lifecycle started at ${to}` : t.kind === 2 ? `Rolled back from ${from} to ${to}` : gated || t.state !== TRANSITION_STATE.Completed ? `${from} → ${to} (Reviewer gate)` : `${from} → ${to}`;
  return (
    <li className="rv-decision">
      <div className="rv-decision-head">
        <strong>{title}</strong>
        {t.kind !== 0 && (
          <span className={`status-pill tone-${t.state === TRANSITION_STATE.Completed ? 'success' : t.state === TRANSITION_STATE.Rejected ? 'danger' : 'neutral'}`}>
            {t.kind === 2 ? 'Rollback' : gated || t.state !== TRANSITION_STATE.Completed ? TRANSITION_STATE_LABEL[t.state] : 'Advanced'}
          </span>
        )}
        {t.isOverride && <span className="status-pill tone-warning">Administrator override</span>}
      </div>
      <dl className="rv-kv">
        <div>
          <dt>{gated ? 'Requested by' : 'By'}</dt>
          <dd>
            {t.requestedByUserId === 'system' ? 'System (lifecycle tracking introduced)' : who(t.requestedByUserId, t.requestedByRole)} · {formatDateTime(t.requestedAt)}
          </dd>
        </div>
        {t.requestNote && (
          <div>
            <dt>{t.kind === 2 ? 'Reason' : 'Note'}</dt>
            <dd>{t.requestNote}</dd>
          </div>
        )}
        {gated && (
          <div>
            <dt>Decided by</dt>
            <dd>
              {who(t.decidedByUserId, t.decidedByRole)} · {formatDateTime(t.decidedAt)}
            </dd>
          </div>
        )}
        {t.decisionReason && (
          <div>
            <dt>Decision reason</dt>
            <dd>{t.decisionReason}</dd>
          </div>
        )}
        {t.overrideReason && (
          <div>
            <dt>Override reason</dt>
            <dd>{t.overrideReason}</dd>
          </div>
        )}
        {t.reviews.filter((r) => r.decision === REVIEW_DECISION.CommentOnly).map((r) => (
          <div key={r.id}>
            <dt>Comment</dt>
            <dd>
              “{r.comment}” — {who(r.reviewerUserId, r.reviewerRole)}, {formatDateTime(r.reviewedAt)}
            </dd>
          </div>
        ))}
      </dl>
      {evidenceNote && (
        <div className="alert alert-info" role="note">
          {evidenceNote}
        </div>
      )}
    </li>
  );
}

function StageDetail({ review, stage, who, onOpen }: { review: LifecycleReview; stage: StageReviewSummary; who: Who; onOpen: (id: string) => void }) {
  const a = stage.activity;
  const created = artifactsCreatedIn(review, stage.stage);
  const byId = artifactsById(review);
  const touched = a.touchedArtifactIds.map((id) => byId.get(id)).filter((x): x is ReviewedArtifact => !!x);
  const transitions = allTransitions(review);
  const entries = transitions.filter((t) => t.toStatus === stage.stage && t.state === TRANSITION_STATE.Completed);
  const exits = transitions.filter((t) => t.fromStatus === stage.stage);
  const events = filterTimeline(review.timeline, 'all', stage.stage);
  const description = LIFECYCLE_STAGES.find((s) => s.status === stage.stage)?.description;

  return (
    <section className="panel" aria-labelledby="rv-stage-title">
      <header className="panel-head">
        <div>
          <span className="eyebrow">
            Stage {stage.stage + 1} of {LIFECYCLE_STAGES.length}
          </span>
          <h2 id="rv-stage-title">{stage.label}</h2>
          <p>{description}</p>
        </div>
        <StatePill state={stage.state} />
      </header>

      <dl className="rv-kv rv-summary">
        <div>
          <dt>Owner</dt>
          <dd>{roleLabel(stage.ownerRole)}</dd>
        </div>
        <div>
          <dt>First entered</dt>
          <dd>{formatDateTime(stage.firstEnteredAt)}</dd>
        </div>
        <div>
          <dt>{stage.stage === LIFECYCLE_STAGES.length - 1 ? 'Reached' : 'Completed'}</dt>
          <dd>{stage.completedAt ? formatDateTime(stage.completedAt) : stage.state === STAGE_REVIEW_STATE.Completed ? NOT_AVAILABLE : '—'}</dd>
        </div>
        <div>
          <dt>Artifacts created</dt>
          <dd>
            {a.artifactsCreated} <span className="muted">({a.aiGenerated} AI · {a.manual} manual)</span>
          </dd>
        </div>
        <div>
          <dt>AI operations</dt>
          <dd>
            {a.aiOperations}
            {a.agentStepsRun > 0 && ` · ${a.agentStepsRun} agent steps`}
          </dd>
        </div>
        <div>
          <dt>Human reviews</dt>
          <dd>
            {a.approvals} approved · {a.rejections} rejected · {a.comments} comments
          </dd>
        </div>
        {a.versionsRecorded > 0 && (
          <div>
            <dt>Edits</dt>
            <dd>
              {a.versionsRecorded} new versions of {a.artifactsEdited} artifacts
            </dd>
          </div>
        )}
        {(a.tasksCompleted > 0 || stage.stage === 5) && (
          <div>
            <dt>Tasks completed</dt>
            <dd>{a.tasksCompleted}</dd>
          </div>
        )}
        {(a.testsPassed + a.testsFailed + a.testsBlocked > 0 || stage.stage === 6) && (
          <div>
            <dt>Test results recorded</dt>
            <dd>
              {a.testsPassed} passed · {a.testsFailed} failed{a.testsBlocked > 0 && ` · ${a.testsBlocked} blocked`}
            </dd>
          </div>
        )}
        {a.requirementSourcesAdded > 0 && (
          <div>
            <dt>Requirement sources added</dt>
            <dd>{a.requirementSourcesAdded}</dd>
          </div>
        )}
      </dl>

      {stage.historyUnavailable && (
        <div className="alert alert-info" role="note">
          The project passed this stage before lifecycle tracking was recorded, so when it happened and what it produced can't be determined.
        </div>
      )}

      {stage.visits.length > 0 && (
        <div className="rv-section">
          <h3>Time in this stage</h3>
          <ol className="rv-visits">
            {stage.visits.map((v, i) => (
              <li key={v.enteredAt}>
                <IconClock width={13} height={13} aria-hidden="true" />
                <span>
                  {stage.visits.length > 1 && <strong>Visit {i + 1}: </strong>}
                  {formatDateTime(v.enteredAt)} → {v.leftAt ? formatDateTime(v.leftAt) : 'now'}
                  {v.leftAt && <span className="muted"> ({formatDuration(v.enteredAt, v.leftAt)})</span>}
                  {v.enteredBy === 2 && <span className="status-pill tone-warning rv-inline-pill">entered by rollback</span>}
                  {v.leftBy === 2 && <span className="status-pill tone-warning rv-inline-pill">left by rollback</span>}
                </span>
              </li>
            ))}
          </ol>
        </div>
      )}

      {stage.stage === 0 && <DefinitionBlock review={review} who={who} />}

      {(entries.some((t) => t.kind !== 0) || exits.length > 0) && (
        <div className="rv-section">
          <h3>Stage decisions</h3>
          <ul className="rv-decisions">
            {[...entries.filter((t) => t.kind !== 0), ...exits.filter((t) => !entries.includes(t))]
              .sort((x, y) => x.sequence - y.sequence)
              .map((t) => (
                <TransitionRow key={t.id} t={t} who={who} />
              ))}
          </ul>
        </div>
      )}

      <div className="rv-section">
        <h3>Artifacts produced in this stage</h3>
        {created.length === 0 ? (
          <p className="muted">
            {stage.historyUnavailable ? 'Not available.' : 'No artifacts were created while the project was in this stage.'}
            {touched.length > 0 && ' Work on earlier artifacts is listed below.'}
          </p>
        ) : (
          <ArtifactTable artifacts={created} who={who} onOpen={onOpen} />
        )}
      </div>

      {touched.length > 0 && (
        <div className="rv-section">
          <h3>Earlier artifacts reviewed, edited or worked on in this stage</h3>
          <ArtifactTable artifacts={touched} who={who} onOpen={onOpen} compact />
        </div>
      )}

      {events.length > 0 && (
        <div className="rv-section">
          <h3>What happened in this stage</h3>
          <EventList events={events} review={review} who={who} onOpen={onOpen} limit={25} />
        </div>
      )}
    </section>
  );
}

function DefinitionBlock({ review, who }: { review: LifecycleReview; who: Who }) {
  const d = review.definition;
  const field = (label: string, value: string | null | undefined) => (
    <div>
      <dt>{label}</dt>
      <dd>{value?.trim() ? value : NOT_AVAILABLE}</dd>
    </div>
  );
  return (
    <div className="rv-section">
      <h3>Project definition</h3>
      <p className="muted">These are the project's current values. Project fields keep no version history, so earlier wording can't be shown.</p>
      <dl className="rv-kv">
        {field('Business problem', d.businessProblem)}
        {field('Objectives', d.objectives)}
        {field('Scope', d.scope)}
        {field('Description', d.description)}
        <div>
          <dt>Created</dt>
          <dd>
            {who(d.createdByUserId)} · {formatDateTime(d.createdAt)}
          </dd>
        </div>
        <div>
          <dt>Last changed</dt>
          <dd>{formatDateTime(d.updatedAt)}</dd>
        </div>
      </dl>
    </div>
  );
}

function UnattributedDetail({ review, who, onOpen }: { review: LifecycleReview; who: Who; onOpen: (id: string) => void }) {
  const artifacts = review.artifacts.filter((a) => a.createdInStage == null);
  return (
    <section className="panel" aria-labelledby="rv-none-title">
      <header className="panel-head">
        <div>
          <h2 id="rv-none-title">{STAGE_UNAVAILABLE}</h2>
          <p>These artifacts were created before the project's recorded lifecycle history begins, so the stage they were produced in can't be determined.</p>
        </div>
      </header>
      <ArtifactTable artifacts={artifacts} who={who} onOpen={onOpen} />
    </section>
  );
}

function ArtifactTable({ artifacts, who, onOpen, compact }: { artifacts: ReviewedArtifact[]; who: Who; onOpen: (id: string) => void; compact?: boolean }) {
  const [type, setType] = useState<number | 'all'>('all');
  const [query, setQuery] = useState('');
  const types = useMemo(() => [...new Set(artifacts.map((a) => a.artifactType))].sort((x, y) => x - y), [artifacts]);
  const q = query.trim().toLowerCase();
  const shown = artifacts.filter((a) => (type === 'all' || a.artifactType === type) && (!q || a.code.toLowerCase().includes(q) || a.title.toLowerCase().includes(q)));

  return (
    <>
      {!compact && (
        <div className="rv-type-chips" role="group" aria-label="Filter by artifact type">
          <button type="button" className={`rv-chip${type === 'all' ? ' active' : ''}`} aria-pressed={type === 'all'} onClick={() => setType('all')}>
            All <span>{artifacts.length}</span>
          </button>
          {types.map((ty) => {
            const n = artifacts.filter((a) => a.artifactType === ty).length;
            return (
              <button key={ty} type="button" className={`rv-chip${type === ty ? ' active' : ''}`} aria-pressed={type === ty} onClick={() => setType(ty)}>
                {artifactTypeName(ty, n !== 1)} <span>{n}</span>
              </button>
            );
          })}
          {artifacts.length > 12 && (
            <input className="rv-search" type="search" placeholder="Filter by code or title" value={query} onChange={(e) => setQuery(e.target.value)} aria-label="Filter artifacts by code or title" />
          )}
        </div>
      )}
      <div className="table-scroll">
        <table className="data-table rv-table">
          <thead>
            <tr>
              <th scope="col">Artifact</th>
              <th scope="col">Type</th>
              <th scope="col">Status</th>
              <th scope="col">Created by</th>
              <th scope="col">Created</th>
              <th scope="col">Source</th>
              <th scope="col">Version</th>
            </tr>
          </thead>
          <tbody>
            {shown.map((a) => {
              const upstream = a.links.filter((l) => l.isOutgoing && l.relationshipType !== 3);
              return (
                <tr key={a.id}>
                  <th scope="row">
                    <button type="button" className="link-button rv-artifact-link" onClick={() => onOpen(a.id)}>
                      <span className="code">{a.code}</span> {a.title}
                    </button>
                  </th>
                  <td>{artifactTypeName(a.artifactType)}</td>
                  <td>{ARTIFACT_STATUS_LABELS[a.status] ?? a.status}</td>
                  <td>
                    <span className={`origin-pill origin-${a.origin === 0 ? 'ai' : 'human'}`}>{a.origin === 0 ? 'AI' : 'Manual'}</span> {who(a.ai?.userId ?? a.createdByUserId)}
                  </td>
                  <td className="rv-nowrap">{formatDateTime(a.createdAt)}</td>
                  <td>
                    {upstream.length > 0
                      ? upstream
                          .slice(0, 3)
                          .map((l) => l.code)
                          .join(', ') + (upstream.length > 3 ? ` +${upstream.length - 3}` : '')
                      : a.requirementSourceId
                        ? 'Requirement source'
                        : a.ai
                          ? humanize(a.ai.operationType)
                          : NOT_AVAILABLE}
                  </td>
                  <td>
                    v{a.currentVersion}
                    {a.approvedVersion != null && a.approvedVersion !== a.currentVersion && <span className="muted"> (approved v{a.approvedVersion})</span>}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      {shown.length === 0 && <p className="muted">No artifacts match this filter.</p>}
    </>
  );
}

function EventList({ events, review, who, onOpen, limit }: { events: ReviewTimelineEvent[]; review: LifecycleReview; who: Who; onOpen: (id: string) => void; limit?: number }) {
  const [all, setAll] = useState(false);
  const byId = artifactsById(review);
  const shown = limit && !all ? events.slice(0, limit) : events;
  return (
    <>
      <ol className="rv-events">
        {shown.map((e, i) => (
          <li key={`${e.at}-${i}`} className={`rv-event kind-${e.kind}`}>
            <span className="rv-event-time">{new Date(e.at).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}</span>
            <span className="rv-event-dot" aria-hidden="true" />
            <div className="rv-event-body">
              <div className="rv-event-title">
                {e.title}
                {e.stage == null ? <span className="stage-chip rv-stage-chip unknown">{STAGE_UNAVAILABLE}</span> : <span className="stage-chip rv-stage-chip">{stageLabel(e.stage)}</span>}
              </div>
              {e.detail && <div className="rv-event-detail">{e.detail}</div>}
              <div className="rv-event-meta">
                {e.actorUserId ? who(e.actorUserId, e.actorRole) : e.kind === 1 ? 'System' : NOT_AVAILABLE}
                {e.artifactIds.length > 0 && (
                  <>
                    {' · '}
                    {e.artifactIds.slice(0, 4).map((id) => (
                      <button key={id} type="button" className="link-button rv-code-link" onClick={() => onOpen(id)}>
                        {byId.get(id)?.code ?? 'artifact'}
                      </button>
                    ))}
                    {e.artifactIds.length > 4 && <span className="muted"> +{e.artifactIds.length - 4}</span>}
                  </>
                )}
              </div>
            </div>
          </li>
        ))}
      </ol>
      {limit && events.length > limit && (
        <button type="button" className="btn btn-ghost btn-sm" onClick={() => setAll((x) => !x)}>
          {all ? 'Show fewer' : `Show all ${events.length} events`}
        </button>
      )}
    </>
  );
}

function Timeline({ review, who, onOpen }: { review: LifecycleReview; who: Who; onOpen: (id: string) => void }) {
  const [filter, setFilter] = useState<TimelineFilter>('all');
  const [stage, setStage] = useState<number | 'all' | 'none'>('all');
  const events = filterTimeline(review.timeline, filter, stage);
  const days = groupByDay(events);

  return (
    <section className="panel" aria-labelledby="rv-timeline-title">
      <header className="panel-head">
        <div>
          <h2 id="rv-timeline-title">Project timeline</h2>
          <p>Everything recorded, oldest first. Bursts, such as one AI call producing ten requirements or a batch approval, are shown as one entry.</p>
        </div>
      </header>
      <div className="toolbar">
        <label className="toolbar-select">
          Show
          <select value={filter} onChange={(e) => setFilter(e.target.value as TimelineFilter)}>
            <option value="all">All events</option>
            <option value="lifecycle">Lifecycle moves and sources</option>
            <option value="ai">AI generation and agents</option>
            <option value="reviews">Reviews, edits and manual work</option>
            <option value="work">Task and test outcomes</option>
          </select>
        </label>
        <label className="toolbar-select">
          Stage
          <select value={String(stage)} onChange={(e) => setStage(e.target.value === 'all' || e.target.value === 'none' ? e.target.value : Number(e.target.value))}>
            <option value="all">All stages</option>
            {LIFECYCLE_STAGES.map((s) => (
              <option key={s.status} value={s.status}>
                {s.label}
              </option>
            ))}
            {review.timeline.some((e) => e.stage == null) && <option value="none">{STAGE_UNAVAILABLE}</option>}
          </select>
        </label>
        <span className="muted">{events.length} events</span>
      </div>
      {days.length === 0 ? (
        <EmptyState compact icon={<IconClock width={18} height={18} />} title="No events" description="Nothing recorded matches this filter." />
      ) : (
        days.map((d) => (
          <div key={d.day} className="rv-day">
            <h3 className="rv-day-label">{d.day}</h3>
            <EventList events={d.events} review={review} who={who} onOpen={onOpen} />
          </div>
        ))
      )}
    </section>
  );
}

function ArtifactProvenanceDialog({
  review,
  artifactId,
  projectId,
  who,
  onOpen,
  onClose,
}: {
  review: LifecycleReview;
  artifactId: string;
  projectId: string;
  who: Who;
  onOpen: (id: string) => void;
  onClose: () => void;
}) {
  const a = review.artifacts.find((x) => x.id === artifactId);
  if (!a) {
    return (
      <Dialog title="Artifact not available" onClose={onClose}>
        <p className="dialog-text">This artifact is not part of the review you can see.</p>
      </Dialog>
    );
  }

  const source = review.requirementSources.find((s) => s.id === a.requirementSourceId);
  const outgoing = a.links.filter((l) => l.isOutgoing);
  const incoming = a.links.filter((l) => !l.isOutgoing);
  const row = (label: string, value: ReactNode) => (
    <div>
      <dt>{label}</dt>
      <dd>{value}</dd>
    </div>
  );

  return (
    <Dialog
      title={`${a.code} · ${a.title}`}
      description={`${artifactTypeName(a.artifactType)} — provenance and history`}
      onClose={onClose}
      width={860}
      footer={
        <>
          <Link to={phasePath(projectId, workSegment(a.artifactType))} className="btn btn-secondary">
            Open in work area
          </Link>
          <button type="button" className="btn btn-primary" onClick={onClose} data-autofocus>
            Close
          </button>
        </>
      }
    >
      <dl className="rv-kv rv-provenance">
        {row('Artifact ID', <span className="code">{a.id}</span>)}
        {row('Status', ARTIFACT_STATUS_LABELS[a.status] ?? a.status)}
        {row('Version', <>v{a.currentVersion} of {a.versionCount} recorded{a.approvedVersion != null ? ` · approved v${a.approvedVersion}` : ' · never approved'}</>)}
        {row('Origin', a.origin === 0 ? 'AI-generated' : 'Manual')}
        {row('Created', `${who(a.createdByUserId)} · ${formatDateTime(a.createdAt)}`)}
        {row('Last updated', `${a.updatedByUserId ? who(a.updatedByUserId) : NOT_AVAILABLE} · ${formatDateTime(a.updatedAt)}`)}
        {row('Produced in stage', stageOrUnavailable(a.createdInStage))}
        {row('Serves stage', stageLabel(a.servesStage))}
        {row(
          'AI operation',
          a.ai ? (
            <>
              {humanize(a.ai.operationType)} · {a.ai.model}
              {a.ai.promptTemplateVersion && ` · ${a.ai.promptTemplateVersion}`} · run by {who(a.ai.userId)} at {formatDateTime(a.ai.timestamp)}
              {a.ai.accepted != null && ` · ${a.ai.accepted ? 'accepted' : 'rejected'} by a reviewer`}
            </>
          ) : a.origin === 0 ? (
            NOT_AVAILABLE
          ) : (
            'None (created by a person)'
          ),
        )}
        {a.ai?.agentRunId && row('Agent', `${humanize(['Requirements', 'Analysis', 'Architecture', 'DevelopmentPlanning', 'QA', 'Review'][a.ai.agent ?? 0] ?? 'Agent')} agent`)}
        {row(
          'Requirement source',
          source ? `${source.fileName ?? ['Manual entry', 'Pasted text', 'Uploaded file'][source.sourceType] ?? 'Source'} · ${formatDateTime(source.createdAt)}` : a.requirementSourceId ? 'Requirement source' : NOT_AVAILABLE,
        )}
        {row(
          'Latest review',
          a.latestReview ? (
            <>
              {['Approved', 'Rejected', 'Comment'][a.latestReview.decision]} by {who(a.latestReview.reviewerUserId, a.latestReview.reviewerRole)} · {formatDateTime(a.latestReview.reviewedAt)}
              {a.latestReview.comment && <> — “{a.latestReview.comment}”</>}
              {a.reviewCount > 1 && <span className="muted"> ({a.reviewCount} reviews)</span>}
            </>
          ) : (
            NOT_AVAILABLE
          ),
        )}
        {a.latestWork &&
          row(
            'Latest outcome',
            <>
              {WORK_OUTCOME_LABEL[a.latestWork.outcome]} by {who(a.latestWork.recordedByUserId, a.latestWork.recordedByRole)} · {formatDateTime(a.latestWork.recordedAt)} (v{a.latestWork.versionNumber})
            </>,
          )}
      </dl>

      {(outgoing.length > 0 || incoming.length > 0) && (
        <div className="rv-links">
          {[
            { label: 'Traces to', items: outgoing },
            { label: 'Traced from', items: incoming },
          ]
            .filter((g) => g.items.length > 0)
            .map((g) => (
              <div key={g.label}>
                <h4>{g.label}</h4>
                <ul>
                  {g.items.map((l) => (
                    <li key={`${l.relatedArtifactId}-${l.relationshipType}`}>
                      <span className="muted">{RELATIONSHIP_TYPE_LABELS[l.relationshipType]}</span>{' '}
                      <button type="button" className="link-button rv-code-link" onClick={() => onOpen(l.relatedArtifactId)}>
                        {l.code}
                      </button>{' '}
                      <span className="muted">{artifactTypeName(l.artifactType)}</span>
                    </li>
                  ))}
                </ul>
              </div>
            ))}
        </div>
      )}

      <ArtifactDetails key={a.id} artifactId={a.id} artifactType={a.artifactType} defaultOpen />
      <p className="muted rv-dialog-note">
        <IconCheckCircle width={12} height={12} /> The review itself changes nothing. The history above is the artifact's own record, as shown in its work area.
      </p>
    </Dialog>
  );
}
