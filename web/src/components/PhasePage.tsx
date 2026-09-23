import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { useProject } from '../context/ProjectContext';
import { deriveSdlc, nextPhase, PHASE_STATE_LABEL, phasePath, previousPhase, SDLC_PHASES } from '../lib/sdlc';
import { PageHeader } from './ui/PageHeader';
import { SdlcStepper } from './SdlcStepper';
import { IconArrowLeft, IconArrowRight, IconSparkles } from './icons';

/**
 * Shared frame for every SDLC phase workspace: title, one-line purpose, live phase state,
 * the project's SDLC track, the phase content, and previous/next phase navigation.
 */
export function PhasePage({
  phaseKey,
  title,
  description,
  actions,
  children,
}: {
  phaseKey: string;
  title: string;
  description: string;
  actions?: ReactNode;
  children: ReactNode;
}) {
  const { project } = useProject();
  const sdlc = deriveSdlc(project.status);
  const phase = SDLC_PHASES.find((p) => p.key === phaseKey);
  const state = sdlc.phases.find((p) => p.key === phaseKey)?.state ?? 'upcoming';
  const prev = previousPhase(phaseKey);
  const next = nextPhase(phaseKey);

  return (
    <>
      <PageHeader
        title={title}
        subtitle={description}
        badge={
          phase && (
            <span className={`phase-badge ${state}`}>
              Step {phase.step} of {SDLC_PHASES.length} · {PHASE_STATE_LABEL[state]}
            </span>
          )
        }
        actions={
          <>
            {actions}
            <Link to={`${phasePath(project.id, 'copilot')}?phase=${phaseKey}`} className="btn btn-ghost">
              <IconSparkles width={15} height={15} /> Ask Copilot
            </Link>
          </>
        }
      />

      <div className="phase-strip">
        <SdlcStepper phases={sdlc.phases} projectId={project.id} activeKey={phaseKey} compact />
      </div>

      <div className="phase-body">{children}</div>

      <nav className="phase-nav" aria-label="Phase navigation">
        {prev ? (
          <Link to={phasePath(project.id, prev.segment)} className="phase-nav-link prev">
            <IconArrowLeft width={16} height={16} />
            <span>
              <small>Previous</small>
              {prev.navLabel}
            </span>
          </Link>
        ) : (
          <span />
        )}
        {next && (
          <Link to={phasePath(project.id, next.segment)} className="phase-nav-link next">
            <span>
              <small>Next phase</small>
              {next.navLabel}
            </span>
            <IconArrowRight width={16} height={16} />
          </Link>
        )}
      </nav>
    </>
  );
}

/** Honest placeholder for a capability the platform has not built yet. Shows structure, never data. */
export function PlannedCard({ icon, title, description, items }: { icon: ReactNode; title: string; description: string; items?: string[] }) {
  return (
    <div className="planned-card">
      <div className="planned-card-head">
        <span className="planned-icon">{icon}</span>
        <div>
          <h3>{title}</h3>
          <p>{description}</p>
        </div>
        <span className="nav-badge nav-badge-planned">Planned</span>
      </div>
      {items && items.length > 0 && (
        <ul className="planned-items">
          {items.map((i) => (
            <li key={i}>{i}</li>
          ))}
        </ul>
      )}
    </div>
  );
}
