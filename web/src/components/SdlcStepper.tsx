import { Link } from 'react-router-dom';
import { PHASE_STATE_LABEL, phasePath, type PhaseProgress } from '../lib/sdlc';
import { IconAlert, IconCheck } from './icons';

/**
 * The AI-REAP signature element: the seven SDLC phases with completed / current / upcoming / blocked
 * state. `horizontal` lays out as a track on wide screens and falls back to a vertical timeline on
 * narrow ones; `compact` drops descriptions and is used inside cards.
 */
export function SdlcStepper({
  phases,
  projectId,
  compact,
  activeKey,
  vertical,
}: {
  phases: PhaseProgress[];
  projectId?: string;
  compact?: boolean;
  /** Phase page the user is currently on (highlighted independently of state). */
  activeKey?: string;
  vertical?: boolean;
}) {
  return (
    <ol className={`sdlc-stepper${compact ? ' compact' : ''}${vertical ? ' vertical' : ''}`} aria-label="SDLC progress">
      {phases.map((p) => {
        const inner = (
          <>
            <span className="sdlc-node" aria-hidden="true">
              {p.state === 'completed' ? <IconCheck width={14} height={14} strokeWidth={2.5} /> : p.state === 'blocked' ? <IconAlert width={14} height={14} /> : p.state === 'current' ? <span className="sdlc-pulse" /> : p.step}
            </span>
            <span className="sdlc-text">
              <span className="sdlc-label">{p.label}</span>
              <span className="sdlc-state">{PHASE_STATE_LABEL[p.state]}</span>
            </span>
          </>
        );
        const cls = `sdlc-step ${p.state}${activeKey === p.key ? ' viewing' : ''}`;
        return (
          <li key={p.key} className={cls} aria-current={p.state === 'current' ? 'step' : undefined}>
            {projectId ? (
              <Link to={phasePath(projectId, p.segment)} className="sdlc-link" aria-label={`${p.label}: ${PHASE_STATE_LABEL[p.state]}`}>
                {inner}
              </Link>
            ) : (
              <div className="sdlc-link" aria-label={`${p.label}: ${PHASE_STATE_LABEL[p.state]}`}>
                {inner}
              </div>
            )}
          </li>
        );
      })}
    </ol>
  );
}

/** Tiny per-phase dot strip for project cards / dashboard rows. */
export function SdlcDots({ phases }: { phases: PhaseProgress[] }) {
  return (
    <ul className="sdlc-dots" aria-hidden="true">
      {phases.map((p) => (
        <li key={p.key} className={p.state} title={`${p.label}: ${PHASE_STATE_LABEL[p.state]}`} />
      ))}
    </ul>
  );
}
