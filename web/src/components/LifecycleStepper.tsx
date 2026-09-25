import { Link } from 'react-router-dom';
import type { LifecycleStagePolicy } from '../api/types';
import { isGatedStage, LIFECYCLE_STAGES, roleLabel, STAGE_STATE_LABEL, stageState } from '../lib/lifecycle';
import { IconAlert, IconCheck, IconLock } from './icons';

/**
 * The eight project lifecycle stages, from Project.status: completed / current / upcoming. The
 * current stage can carry "Awaiting review" (a pending gated request) and earlier stages an
 * evidence-gap warning. Connectors out of Design and Testing are marked as Reviewer gates.
 * AI agent steps never appear here.
 */
export function LifecycleStepper({
  status,
  pending,
  gaps,
  policy,
  compact,
  highlight,
  to,
}: {
  status: number;
  /** A gated request out of the current stage is waiting for a Reviewer. */
  pending?: boolean;
  /** Stages already passed whose exit checks no longer hold. */
  gaps?: number[];
  /** Adds each stage's owner role under its name. */
  policy?: LifecycleStagePolicy[] | null;
  compact?: boolean;
  /** Stages to emphasise (e.g. the ones a work area serves). */
  highlight?: number[];
  /** Makes every stage a link (e.g. to the project overview). */
  to?: string;
}) {
  return (
    <ol className={`sdlc-stepper lifecycle-stepper${compact ? ' compact' : ''}`} aria-label="Project lifecycle">
      {LIFECYCLE_STAGES.map((s) => {
        const state = stageState(s.status, status);
        const gap = gaps?.includes(s.status);
        const awaiting = pending && state === 'current';
        const owner = policy?.find((p) => p.status === s.status)?.ownerRole;
        const stateText = awaiting ? 'Awaiting review' : gap ? 'Evidence gap' : STAGE_STATE_LABEL[state];
        const inner = (
          <>
            <span className="sdlc-node" aria-hidden="true">
              {gap ? <IconAlert width={13} height={13} /> : state === 'completed' ? <IconCheck width={14} height={14} strokeWidth={2.5} /> : state === 'current' ? <span className="sdlc-pulse" /> : s.status + 1}
            </span>
            <span className="sdlc-text">
              <span className="sdlc-label">{s.label}</span>
              <span className="sdlc-state">{stateText}</span>
              {owner && !compact && <span className="lc-owner">{roleLabel(owner)}</span>}
            </span>
          </>
        );
        const cls = `sdlc-step ${state}${awaiting ? ' awaiting' : ''}${gap ? ' gap' : ''}${highlight?.includes(s.status) ? ' viewing' : ''}${isGatedStage(s.status) ? ' gated' : ''}`;
        const label = `Stage ${s.status + 1} of ${LIFECYCLE_STAGES.length}, ${s.label}: ${stateText}${isGatedStage(s.status) ? '. Leaving it needs Reviewer approval' : ''}`;
        return (
          <li key={s.status} className={cls} aria-current={state === 'current' ? 'step' : undefined}>
            {to ? (
              <Link to={to} className="sdlc-link" aria-label={label} title={s.description}>
                {inner}
              </Link>
            ) : (
              <div className="sdlc-link" aria-label={label} title={s.description}>
                {inner}
              </div>
            )}
            {isGatedStage(s.status) && (
              <span className="lc-gate" title="Reviewer approval required to leave this stage" aria-hidden="true">
                <IconLock width={10} height={10} />
              </span>
            )}
          </li>
        );
      })}
    </ol>
  );
}

/** Eight-segment strip for cards and table rows. */
export function LifecycleDots({ status, pending }: { status: number; pending?: boolean }) {
  return (
    <ul className="sdlc-dots" aria-hidden="true">
      {LIFECYCLE_STAGES.map((s) => {
        const state = stageState(s.status, status);
        return <li key={s.status} className={`${state}${pending && state === 'current' ? ' awaiting' : ''}`} title={`${s.label}: ${STAGE_STATE_LABEL[state]}`} />;
      })}
    </ul>
  );
}
