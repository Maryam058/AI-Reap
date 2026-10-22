import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { lifecycleApi } from '../api/lifecycle';
import { projectsApi } from '../api/projects';
import { REVIEW_DECISION, TRANSITION_KIND, TRANSITION_STATE, type LifecycleCheck, type Project, type ProjectLifecycleState, type StageTransition } from '../api/types';
import { useLifecyclePolicy, useProjectLifecycle } from '../hooks/useLifecycle';
import { personLabel, useMemberNames } from '../hooks/useMemberNames';
import { LAST_STAGE, latestRejection, lifecycleProgress, LIFECYCLE_STAGES, roleLabel, stageLabel, submissionEvidenceNote } from '../lib/lifecycle';
import { apiErrorText, failingChecksOf } from '../lib/errors';
import { formatRelativeTime } from '../lib/format';
import { useToast } from '../context/ToastContext';
import { Dialog } from './ui/Dialog';
import { LifecycleLegend, LifecycleStepper } from './LifecycleStepper';
import { CheckGuidance } from './CheckGuidance';
import { RoleRequiredActions } from './RoleRequiredAlert';
import { roleGuard } from '../lib/roleGuard';
import { createHandoffTracker, createMembershipFollowUps, resolveHandoffPopup, stageHandoff, type HandoffPopup } from '../lib/stageHandoff';
import { phasePath } from '../lib/workAreas';
import { StageHandoffDialog } from './StageHandoffDialog';
import { NextRoleMemberDialog } from './NextRoleMemberDialog';
import { IconAlert, IconArrowRight, IconCheck, IconChevronDown, IconLock, IconRotateCcw, IconX } from './icons';

// Mirrors ProjectLifecyclePolicy.GateApproverRole.
const ProjectGateApprover = 'Reviewer';

type DialogMode = 'advance' | 'submit' | 'approve' | 'reject' | 'withdraw' | 'rollback';

// Module-level so a move's hand-off shows once, even if the panel re-renders or remounts.
const handoffs = createHandoffTracker();
// Moves whose next role had no project member when the user left to manage members; rechecked on return.
const memberFollowUps = createMembershipFollowUps();

/**
 * The project's lifecycle (§5) on the overview: the eight stages, where the project is, what the
 * current stage still needs, who acts next, any pending Reviewer request, and the full history.
 * Every rule - ownership, gates, evidence, separation of duties, override, rollback - is the
 * backend's; this panel shows the backend's answers and offers only the actions it allows.
 */
export function LifecyclePanel({ project, version, onProjectChanged }: { project: Project; version?: unknown; onProjectChanged: () => Promise<void> | void }) {
  const { token, userId, roles, hasRole } = useAuth();
  const navigate = useNavigate();
  const { toast } = useToast();
  const policy = useLifecyclePolicy();
  const names = useMemberNames(project.id);
  const { state, failed, reload } = useProjectLifecycle(project.id, `${version ?? ''}|${project.status}|${project.updatedAt}`);
  const [dialog, setDialog] = useState<DialogMode | null>(null);
  // The popup after a completed move, with the move itself so it can be re-judged later.
  const [handoff, setHandoff] = useState<{ popup: HandoffPopup; moved: StageTransition } | null>(null);
  const [checkingMembers, setCheckingMembers] = useState(false);

  // The hand-off for `moved`, judged on lifecycle state and members fetched now; null when there's
  // nothing to hand off (e.g. the project has moved on since). Lifecycle errors propagate.
  const judgeHandoff = async (moved: StageTransition) => {
    const next = stageHandoff(moved, await lifecycleApi.get(project.id, token), roles);
    return next && { moved, popup: await resolveHandoffPopup(next, () => projectsApi.getMembers(project.id, token)) };
  };

  const recheckMembers = async (moved: StageTransition, previous: HandoffPopup) => {
    setCheckingMembers(true);
    try {
      setHandoff(await judgeHandoff(moved));
    } catch {
      // Couldn't re-fetch the lifecycle: say so rather than claiming anything about members.
      setHandoff({ moved, popup: { kind: 'membership-error', handoff: previous.handoff } });
    } finally {
      setCheckingMembers(false);
    }
  };

  // Back from managing members: re-judge the remembered move once, on fresh server data.
  useEffect(() => {
    const moved = memberFollowUps.peek(project.id);
    if (!moved) return;
    let cancelled = false;
    judgeHandoff(moved)
      .then((h) => {
        if (cancelled) return;
        memberFollowUps.forget(project.id);
        setHandoff(h);
      })
      .catch(() => {
        if (cancelled) return;
        memberFollowUps.forget(project.id);
      });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project.id]);

  if (failed && !state) {
    return (
      <section className="panel lifecycle-panel" id="lifecycle">
        <h2 className="panel-title">Project lifecycle</h2>
        <p className="muted">The lifecycle could not be loaded right now.</p>
        <button type="button" className="btn btn-secondary btn-sm" onClick={reload}>
          Try again
        </button>
      </section>
    );
  }

  if (!state) {
    return (
      <section className="panel lifecycle-panel" id="lifecycle" aria-busy="true">
        <div className="skeleton skeleton-panel" />
      </section>
    );
  }

  const progress = lifecycleProgress(state.status);
  const pending = state.pendingTransition ?? null;
  const rejection = pending ? null : latestRejection(state.history, state.status);
  const next = state.nextStatus ?? null;
  const nextOwner = policy?.find((p) => p.status === next)?.ownerRole;
  const passed = state.exitChecks.filter((c) => c.passed).length;
  const { actions } = state;
  const mayMove = actions.canAdvance || actions.canSubmitForApproval;
  // Failing checks, or an Administrator acting in the owner's place: either way only an override moves it.
  const moveIsOverride = (!state.readyToAdvance && actions.canOverride) || actions.advanceIsOverride;
  // `moved` is the transition the backend completed (never a pending request, rejection or
  // rollback); only then is the next role shown, judged from the lifecycle state fetched afresh.
  const done = async (message: string, tone: 'success' | 'info' = 'success', moved?: StageTransition) => {
    toast(message, tone);
    setDialog(null);
    reload();
    await onProjectChanged();
    if (!moved) return;
    try {
      const next = await judgeHandoff(moved);
      if (next && handoffs.claim(next.popup.handoff.transitionId)) setHandoff(next);
    } catch {
      // The move itself succeeded and was reported; the hand-off is guidance only.
    }
  };

  return (
    <section className="panel lifecycle-panel" id="lifecycle" aria-labelledby="lifecycle-title">
      <header className="panel-head">
        <div>
          <span className="eyebrow">Project lifecycle</span>
          <h2 id="lifecycle-title">
            Stage {progress.stageNumber} of {LIFECYCLE_STAGES.length}: {state.stageLabel}
          </h2>
          <p>
            {progress.finished ? (
              'Delivery approved. The lifecycle is complete.'
            ) : (
              <>
                Owned by <strong>{roleLabel(state.ownerRole)}</strong>
                {pending ? (
                  <>
                    {' '}· <strong className="text-warning">awaiting Reviewer approval</strong> to move to {stageLabel(pending.toStatus)}
                  </>
                ) : (
                  <> · next: {stageLabel(next)}</>
                )}
              </>
            )}
          </p>
        </div>
        <div className="big-percent" aria-label={`Lifecycle progress ${progress.percent}%`}>
          {progress.percent}
          <span>%</span>
        </div>
      </header>

      {state.evidenceGaps.length > 0 && (
        <div className="alert alert-warning" role="status">
          <IconAlert width={16} height={16} />
          <span>
            <strong>Evidence gap.</strong> The project is in {state.stageLabel}, but the exit checks of{' '}
            {state.evidenceGaps.map((g) => `${g.label} (${g.checks.filter((c) => !c.passed).length} failing)`).join(', ')} no longer hold. The stage is not changed
            automatically; address the gaps, or an Administrator can roll the project back.
          </span>
        </div>
      )}
      {state.evidenceGaps.length > 0 && (
        <details className="lc-gaps">
          <summary>Show what's missing in earlier stages</summary>
          {state.evidenceGaps.map((g) => (
            <div key={g.stage} className="lc-gap-stage">
              <h4>{g.label}</h4>
              <CheckList checks={g.checks.filter((c) => !c.passed)} projectId={project.id} canOverride={actions.canOverride} expandFirst={false} />
            </div>
          ))}
        </details>
      )}

      <div className="lc-stepper-wrap">
        <LifecycleStepper status={state.status} pending={!!pending} gaps={state.evidenceGaps.map((g) => g.stage)} policy={policy} />
      </div>
      <LifecycleLegend />

      {!progress.finished && next !== null && (
        <div className="lc-grid">
          <div className="lc-card">
            <div className="lc-card-head">
              <h3>
                {state.stageLabel} <IconArrowRight width={14} height={14} /> {stageLabel(next)}
              </h3>
              {state.nextIsGated ? (
                <span className="status-pill tone-warning">
                  <IconLock width={11} height={11} /> Reviewer gate
                </span>
              ) : (
                <span className="status-pill tone-info">Direct move</span>
              )}
            </div>
            <p className="lc-explain">
              {state.nextIsGated
                ? `${roleLabel(state.ownerRole)} submits the stage for approval; a Reviewer (not the submitter) approves or rejects it. The project stays in ${state.stageLabel} until then.`
                : `${roleLabel(state.ownerRole)} moves the project to ${stageLabel(next)} once every check passes.${nextOwner ? ` ${roleLabel(nextOwner)} then owns it.` : ''}`}
              {state.requiresSignOff && ' QA signs off with a note when submitting.'}
            </p>

            <div className="lc-checks-head">
              <strong>Exit checks</strong>
              <span className={`status-pill tone-${state.readyToAdvance ? 'success' : 'warning'}`}>
                {passed}/{state.exitChecks.length} met
              </span>
            </div>
            <CheckList checks={state.exitChecks} projectId={project.id} canOverride={actions.canOverride} />

            {rejection && <RejectionNote transition={rejection} mine={rejection.requestedByUserId === userId} by={personLabel(rejection.decidedByUserId, rejection.decidedByRole, names, userId)} />}

            {!pending && (
              <div className="lc-actions">
                {mayMove ? (
                  <>
                    <button
                      type="button"
                      className={`btn ${state.readyToAdvance && !moveIsOverride ? 'btn-primary' : 'btn-secondary'}`}
                      disabled={!state.readyToAdvance && !actions.canOverride}
                      onClick={() => setDialog(actions.canSubmitForApproval ? 'submit' : 'advance')}
                    >
                      {moveIsOverride
                        ? actions.canSubmitForApproval
                          ? 'Submit with override…'
                          : 'Override and move…'
                        : actions.canSubmitForApproval
                          ? 'Submit for Reviewer approval'
                          : `Move to ${stageLabel(next)}`}
                    </button>
                    {!state.readyToAdvance && !actions.canOverride && <span className="muted">Available once every exit check passes.</span>}
                  </>
                ) : (
                  <>
                    {/* Neither the stage owner nor an Administrator (who gets the labelled override above): explain who moves it. */}
                    {roleGuard(state.ownerRole, roles).kind === 'blocked' && (
                      <RoleRequiredActions
                        requiredRole={state.ownerRole}
                        actions={[{ label: state.nextIsGated ? 'Submit for Reviewer approval' : `Move to ${stageLabel(next)}`, className: 'btn btn-secondary' }]}
                      />
                    )}
                    <span className="muted">
                      Waiting for <strong>{roleLabel(state.ownerRole)}</strong> to {state.nextIsGated ? 'submit this stage for approval' : 'move the project on'}.
                    </span>
                  </>
                )}
              </div>
            )}
          </div>

          {pending && <PendingRequest state={state} pending={pending} names={names} onDecide={setDialog} onChanged={reload} />}
        </div>
      )}

      <div className="lc-footer">
        <History history={state.history} userId={userId} names={names} />
        {actions.canRollback && (
          <button type="button" className="btn btn-ghost btn-sm lc-rollback-btn" onClick={() => setDialog('rollback')}>
            <IconRotateCcw width={14} height={14} /> Roll back…
          </button>
        )}
      </div>

      {dialog && (
        <TransitionDialog
          mode={dialog}
          state={state}
          projectId={project.id}
          token={token}
          onClose={() => setDialog(null)}
          onDone={done}
        />
      )}

      {handoff &&
        (handoff.popup.kind === 'handoff' ? (
          <StageHandoffDialog handoff={handoff.popup.handoff} onClose={() => setHandoff(null)} />
        ) : (
          <NextRoleMemberDialog
            handoff={handoff.popup.handoff}
            problem={handoff.popup.kind === 'missing-member' ? 'missing' : 'error'}
            canManage={hasRole('Administrator') || hasRole('BusinessAnalyst')}
            checking={checkingMembers}
            onRecheck={() => void recheckMembers(handoff.moved, handoff.popup)}
            onManage={() => {
              memberFollowUps.remember(handoff.moved);
              setHandoff(null);
              navigate(`${phasePath(project.id, 'gathering')}?tab=members`);
            }}
            onClose={() => setHandoff(null)}
          />
        ))}
    </section>
  );
}

function CheckList({
  checks,
  projectId,
  canOverride = false,
  expandFirst = true,
}: {
  checks: LifecycleCheck[];
  projectId: string;
  canOverride?: boolean;
  /** Open the first failing check's guidance by default. */
  expandFirst?: boolean;
}) {
  if (checks.length === 0) return <p className="muted">This stage has no exit checks.</p>;
  // Failing checks first: they are what the owner has to act on.
  const ordered = [...checks].sort((a, b) => Number(a.passed) - Number(b.passed));
  const firstWithGuidance = expandFirst ? ordered.find((c) => !c.passed && c.guidance)?.label : undefined;
  // A check this one follows from is named only while it is failing too (then it carries its key).
  const labelFor = (key: string) => checks.find((c) => c.guidance?.key === key)?.label;
  return (
    <ul className="health-list lc-checks">
      {ordered.map((c) => (
        <CheckRow key={c.label} check={c} projectId={projectId} canOverride={canOverride} defaultOpen={c.label === firstWithGuidance} labelFor={labelFor} />
      ))}
    </ul>
  );
}

// One check. A failing check with guidance expands into "how to meet this"; anything else - a
// passed check, or a check the backend sends without guidance (e.g. a key this client does not
// know) - shows its label and detail exactly as before.
function CheckRow({
  check,
  projectId,
  canOverride,
  defaultOpen,
  labelFor,
}: {
  check: LifecycleCheck;
  projectId: string;
  canOverride: boolean;
  defaultOpen: boolean;
  labelFor: (key: string) => string | undefined;
}) {
  const [open, setOpen] = useState(defaultOpen);
  const guidance = !check.passed && check.guidance && Array.isArray(check.guidance.blockers) ? check.guidance : null;
  return (
    <li className={guidance ? 'lc-check has-guidance' : 'lc-check'}>
      <span className={`health-dot ${check.passed ? 'good' : 'bad'}`} aria-hidden="true" />
      <span className="health-label">
        <strong>{check.label}</strong>
        <small>{check.detail}</small>
        {guidance && (
          <button type="button" className="lc-howto-toggle" aria-expanded={open} onClick={() => setOpen((v) => !v)}>
            {open ? 'Hide how to meet this' : 'How to meet this'}
            <IconChevronDown width={12} height={12} className={open ? 'is-open' : undefined} />
          </button>
        )}
        {guidance && open && <CheckGuidance projectId={projectId} guidance={guidance} canOverride={canOverride} labelFor={labelFor} />}
      </span>
      <span className="sr-only">{check.passed ? 'Met' : 'Not met'}</span>
    </li>
  );
}

function RejectionNote({ transition, mine, by }: { transition: StageTransition; mine: boolean; by: string }) {
  return (
    <div className="alert alert-danger lc-rejection" role="status">
      <IconX width={16} height={16} />
      <span>
        <strong>{mine ? 'Your request' : 'The last request'} to move to {stageLabel(transition.toStatus)} was rejected</strong>
        {` by ${by}`}
        {transition.decidedAt && ` ${formatRelativeTime(transition.decidedAt)}`}: “{transition.decisionReason}”
      </span>
    </div>
  );
}

function PendingRequest({
  state,
  pending,
  names,
  onDecide,
  onChanged,
}: {
  state: ProjectLifecycleState;
  pending: StageTransition;
  names: Map<string, string>;
  onDecide: (mode: DialogMode) => void;
  onChanged: () => void;
}) {
  const { token, userId, roles } = useAuth();
  const { toast } = useToast();
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);
  const mine = pending.requestedByUserId === userId;
  const comments = pending.reviews.filter((r) => r.decision === REVIEW_DECISION.CommentOnly);

  const addComment = async () => {
    setBusy(true);
    try {
      await lifecycleApi.comment(pending.id, comment.trim(), token);
      setComment('');
      onChanged();
    } catch (err) {
      toast(apiErrorText(err, 'Could not add the comment.'), 'error');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="lc-card lc-pending" aria-labelledby="pending-title">
      <div className="lc-card-head">
        <h3 id="pending-title">Stage transition request</h3>
        <span className="status-pill tone-warning">Awaiting Reviewer</span>
      </div>
      <dl className="kv lc-kv">
        <div>
          <dt>Request</dt>
          <dd>
            {stageLabel(pending.fromStatus)} → {stageLabel(pending.toStatus)}
          </dd>
        </div>
        <div>
          <dt>Submitted by</dt>
          <dd>
            {personLabel(pending.requestedByUserId, pending.requestedByRole, names, userId)} · {formatRelativeTime(pending.requestedAt)}
          </dd>
        </div>
        <div>
          <dt>Must be decided by</dt>
          <dd>A Reviewer{mine ? ' other than you' : ' who did not submit it'} (or an Administrator)</dd>
        </div>
        <div>
          <dt>Project stage</dt>
          <dd>Still {state.stageLabel} until approved</dd>
        </div>
      </dl>
      {pending.requestNote && <blockquote className="lc-note">{pending.requestNote}</blockquote>}
      {pending.isOverride && (
        <p className="lc-override">
          <IconAlert width={13} height={13} /> Submitted with an Administrator override: {pending.overrideReason}
        </p>
      )}

      {(state.actions.canDecide || state.actions.canWithdraw) && (
        <div className="lc-actions">
          {state.actions.canDecide && (
            <>
              <button type="button" className="btn btn-primary" onClick={() => onDecide('approve')}>
                <IconCheck width={14} height={14} /> Approve
              </button>
              <button type="button" className="btn btn-danger" onClick={() => onDecide('reject')}>
                Reject…
              </button>
            </>
          )}
          {state.actions.canWithdraw && (
            <button type="button" className="btn btn-secondary" onClick={() => onDecide('withdraw')}>
              Withdraw request
            </button>
          )}
        </div>
      )}
      {/* Neither a Reviewer nor an Administrator (who decides by labelled override): explain who decides. */}
      {!state.actions.canDecide && !mine && roleGuard(ProjectGateApprover, roles).kind === 'blocked' && (
        <div className="lc-actions">
          <RoleRequiredActions
            requiredRole={ProjectGateApprover}
            actions={[
              { label: 'Approve', className: 'btn btn-secondary' },
              { label: 'Reject…', className: 'btn btn-secondary' },
            ]}
          />
        </div>
      )}
      {!state.actions.canDecide && !mine && <p className="muted">Waiting for a Reviewer's decision.</p>}
      {mine && <p className="muted">You can't decide your own request; a Reviewer has been notified.</p>}

      <div className="lc-comments">
        <strong>Comments</strong>
        {comments.length === 0 ? (
          <p className="muted">No comments yet.</p>
        ) : (
          <ul>
            {comments.map((c) => (
              <li key={c.id}>
                <span className="muted">
                  {personLabel(c.reviewerUserId, c.reviewerRole, names, userId)} · {formatRelativeTime(c.reviewedAt)}
                </span>
                <p>{c.comment}</p>
              </li>
            ))}
          </ul>
        )}
        <div className="lc-comment-form">
          <textarea className="req-textarea" rows={2} value={comment} onChange={(e) => setComment(e.target.value)} placeholder="Add a comment (does not approve or reject)" aria-label="Comment on this request" />
          <button type="button" className="btn btn-secondary btn-sm" disabled={busy || !comment.trim()} onClick={addComment}>
            {busy ? 'Posting…' : 'Comment'}
          </button>
        </div>
      </div>
    </div>
  );
}

function History({ history, userId, names }: { history: StageTransition[]; userId: string | null; names: Map<string, string> }) {
  const items = [...history].reverse();
  return (
    <details className="lc-history">
      <summary>Stage transition history ({history.length})</summary>
      <ol>
        {items.map((t) => (
          <li key={t.id} className={`lc-event kind-${t.kind} state-${t.state}${t.isOverride ? ' override' : ''}`}>
            <span className="lc-event-when">{formatRelativeTime(t.decidedAt ?? t.requestedAt)}</span>
            <span className="lc-event-text">{describe(t, userId, names)}</span>
            {t.isOverride && <span className="status-pill tone-danger">Override: {t.overrideReason}</span>}
            {t.kind === TRANSITION_KIND.Rollback && t.requestNote && <span className="muted">Reason: {t.requestNote}</span>}
            {t.state === TRANSITION_STATE.Rejected && t.decisionReason && <span className="muted">Reason: {t.decisionReason}</span>}
            {submissionEvidenceNote(t) && (
              <span className="muted" role="note">
                {submissionEvidenceNote(t)}
              </span>
            )}
          </li>
        ))}
      </ol>
    </details>
  );
}

function describe(t: StageTransition, userId: string | null, names: Map<string, string>): string {
  const who = (id: string | null | undefined, role: string | null | undefined) =>
    id && id === userId ? 'you' : id === 'system' ? 'the system' : personLabel(id, role, names, userId);
  const move = `${stageLabel(t.fromStatus)} → ${stageLabel(t.toStatus)}`;
  if (t.kind === TRANSITION_KIND.Baseline) return `Lifecycle history starts: ${stageLabel(t.toStatus)}`;
  if (t.kind === TRANSITION_KIND.Rollback) return `Rolled back ${move} by ${who(t.decidedByUserId, t.decidedByRole)}`;
  const requested = `${move} requested by ${who(t.requestedByUserId, t.requestedByRole)}`;
  switch (t.state) {
    case TRANSITION_STATE.Pending:
      return `${requested} - awaiting Reviewer`;
    case TRANSITION_STATE.Rejected:
      return `${requested} - rejected by ${who(t.decidedByUserId, t.decidedByRole)}`;
    case TRANSITION_STATE.Withdrawn:
      return `${requested} - withdrawn`;
    default:
      return t.decidedByUserId && t.decidedByUserId !== t.requestedByUserId
        ? `${requested} - approved by ${who(t.decidedByUserId, t.decidedByRole)}`
        : `Moved ${move} by ${who(t.requestedByUserId, t.requestedByRole)}`;
  }
}

function TransitionDialog({
  mode,
  state,
  projectId,
  token,
  onClose,
  onDone,
}: {
  mode: DialogMode;
  state: ProjectLifecycleState;
  projectId: string;
  token: string | null;
  onClose: () => void;
  onDone: (message: string, tone?: 'success' | 'info', moved?: StageTransition) => Promise<void>;
}) {
  const next = state.nextStatus ?? LAST_STAGE;
  const pending = state.pendingTransition ?? null;
  const failingOverride = (mode === 'advance' || mode === 'submit' || mode === 'approve') && !state.readyToAdvance && state.actions.canOverride;
  // An Administrator standing in for the stage owner (or the Reviewer) may act only as an override.
  const standIn = mode === 'approve' ? state.actions.decideIsOverride : mode === 'advance' || mode === 'submit' ? state.actions.advanceIsOverride : false;
  const override = failingOverride || standIn;
  const [note, setNote] = useState('');
  const [reason, setReason] = useState('');
  const [target, setTarget] = useState(Math.max(state.status - 1, 0));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [failing, setFailing] = useState<LifecycleCheck[]>([]);

  const needsReason = override || mode === 'reject' || mode === 'rollback';
  const needsNote = mode === 'submit' && state.requiresSignOff && !override;

  const copy: Record<DialogMode, { title: string; confirm: string; intro: string }> = {
    advance: {
      title: failingOverride ? 'Override failing checks?' : override ? `Move to ${stageLabel(next)} as an override?` : `Move to ${stageLabel(next)}?`,
      confirm: override ? 'Override and move' : `Move to ${stageLabel(next)}`,
      intro: `The project moves from ${state.stageLabel} to ${stageLabel(next)} and the new stage's owners are notified.`,
    },
    submit: {
      title: override ? 'Submit with an override?' : 'Submit for Reviewer approval?',
      confirm: override ? 'Submit with override' : 'Submit request',
      intro: `This sends a request to move from ${state.stageLabel} to ${stageLabel(next)}. The project stays in ${state.stageLabel} until a Reviewer approves it.`,
    },
    approve: {
      title: `Approve moving to ${stageLabel(pending?.toStatus ?? next)}?`,
      confirm: override ? 'Override and approve' : 'Approve',
      intro: `The exit checks are judged again now. On approval the project moves to ${stageLabel(pending?.toStatus ?? next)}.`,
    },
    reject: {
      title: 'Reject this request?',
      confirm: 'Reject request',
      intro: `The project stays in ${state.stageLabel}. The requester is notified with your reason.`,
    },
    withdraw: { title: 'Withdraw this request?', confirm: 'Withdraw', intro: 'The request is closed and kept in the history. The project stays where it is.' },
    rollback: {
      title: 'Roll the project back?',
      confirm: 'Roll back',
      intro: 'A rollback moves the project to an earlier stage. It is not forward progress: it is recorded as a rollback with your reason, and history is kept.',
    },
  };

  const submit = async () => {
    setBusy(true);
    setError(null);
    setFailing([]);
    try {
      switch (mode) {
        case 'advance':
        case 'submit': {
          const t = await lifecycleApi.advance(projectId, { targetStatus: next, note: note.trim() || undefined, overrideReason: override ? reason.trim() : undefined }, token);
          await (t.state === TRANSITION_STATE.Pending
            ? onDone(`Request submitted. The project stays in ${state.stageLabel} until a Reviewer approves it.`, 'info')
            : onDone(`Project moved to ${stageLabel(t.toStatus)}.`, 'success', t));
          break;
        }
        case 'approve': {
          const t = await lifecycleApi.decide(pending!.id, { approve: true, reason: note.trim() || undefined, overrideReason: override ? reason.trim() : undefined }, token);
          await onDone(`Approved. The project moved to ${stageLabel(pending!.toStatus)}.`, 'success', t);
          break;
        }
        case 'reject':
          await lifecycleApi.decide(pending!.id, { approve: false, reason: reason.trim() }, token);
          await onDone(`Request rejected. The project stays in ${state.stageLabel}.`, 'info');
          break;
        case 'withdraw':
          await lifecycleApi.withdraw(pending!.id, note.trim() || undefined, token);
          await onDone('Request withdrawn.', 'info');
          break;
        case 'rollback':
          await lifecycleApi.rollback(projectId, target, reason.trim(), token);
          await onDone(`Project rolled back to ${stageLabel(target)}.`, 'info');
          break;
      }
    } catch (err) {
      setError(apiErrorText(err, 'The change could not be made.'));
      setFailing(failingChecksOf(err));
    } finally {
      setBusy(false);
    }
  };

  const text = copy[mode];
  const disabled = busy || (needsReason && !reason.trim()) || (needsNote && !note.trim());

  return (
    <Dialog
      title={text.title}
      onClose={onClose}
      width={560}
      footer={
        <>
          <button type="button" className="btn btn-secondary" onClick={onClose} disabled={busy}>
            Cancel
          </button>
          <button type="button" className={`btn ${override || mode === 'reject' || mode === 'rollback' ? 'btn-danger' : 'btn-primary'}`} onClick={submit} disabled={disabled} data-autofocus>
            {busy ? 'Working…' : text.confirm}
          </button>
        </>
      }
    >
      <p className="dialog-text">{text.intro}</p>

      {override && (
        <div className="alert alert-warning">
          <IconAlert width={16} height={16} />
          <span>
            <strong>Administrator override.</strong>{' '}
            {failingOverride
              ? `${state.exitChecks.filter((c) => !c.passed).length} exit check(s) are failing. The move is recorded as an override, with your reason and the failing checks.`
              : `You are acting in place of the ${roleLabel(mode === 'approve' ? 'Reviewer' : state.ownerRole)}. The move is recorded as an override, with your reason.`}
          </span>
        </div>
      )}

      {mode === 'rollback' && (
        <label className="field">
          <span className="field-label">Roll back to</span>
          <select className="req-textarea" value={target} onChange={(e) => setTarget(Number(e.target.value))}>
            {LIFECYCLE_STAGES.filter((s) => s.status < state.status).map((s) => (
              <option key={s.status} value={s.status}>
                {s.label}
              </option>
            ))}
          </select>
        </label>
      )}

      {(mode === 'advance' || mode === 'submit' || mode === 'approve' || mode === 'withdraw') && (
        <label className="field">
          <span className="field-label">{needsNote ? 'QA sign-off note (required)' : mode === 'approve' ? 'Approval comment (optional)' : 'Note (optional)'}</span>
          <textarea className="req-textarea" rows={2} value={note} onChange={(e) => setNote(e.target.value)} />
        </label>
      )}

      {needsReason && (
        <label className="field">
          <span className="field-label">{override ? 'Override reason (required)' : mode === 'reject' ? 'Rejection reason (required)' : 'Rollback reason (required)'}</span>
          <textarea className="req-textarea" rows={3} value={reason} onChange={(e) => setReason(e.target.value)} />
        </label>
      )}

      {error && <p className="field-error-text">{error}</p>}
      {failing.length > 0 && <CheckList checks={failing} projectId={projectId} canOverride={state.actions.canOverride} />}
    </Dialog>
  );
}
