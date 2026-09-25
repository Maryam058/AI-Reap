import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { useProject } from '../context/ProjectContext';
import { stageLabel, stageState } from '../lib/lifecycle';
import { nextWorkArea, phasePath, previousWorkArea, workArea } from '../lib/workAreas';
import { PageHeader } from './ui/PageHeader';
import { LifecycleStepper } from './LifecycleStepper';
import { IconArrowLeft, IconArrowRight, IconSparkles } from './icons';

/**
 * Shared frame for every work area (gathering, analysis, validation, design, development, testing,
 * traceability): title, purpose, the lifecycle stage(s) the area's work serves, the project's
 * actual lifecycle position, the content, and previous/next work-area navigation. Work areas are
 * navigation; the lifecycle stage is Project.status and only moves on the project overview.
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
  const area = workArea(phaseKey);
  const serves = area?.serves ?? [];
  // How this area's stages relate to where the project actually is.
  const states = serves.map((s) => stageState(s, project.status));
  const relation = states.includes('current') ? 'current' : states.every((s) => s === 'completed') ? 'completed' : 'upcoming';
  const relationText = relation === 'current' ? 'current stage' : relation === 'completed' ? 'stage already passed' : 'ahead of the project';
  const prev = previousWorkArea(phaseKey);
  const next = nextWorkArea(phaseKey);

  return (
    <>
      <PageHeader
        title={title}
        subtitle={description}
        badge={
          serves.length > 0 && (
            <span className={`phase-badge ${relation}`} title={`The project is in ${stageLabel(project.status)}`}>
              Serves {serves.map(stageLabel).join(' & ')} · {relationText}
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
        <LifecycleStepper status={project.status} compact highlight={serves} to={`/projects/${project.id}#lifecycle`} />
      </div>

      <div className="phase-body">{children}</div>

      <nav className="phase-nav" aria-label="Work area navigation">
        {prev ? (
          <Link to={phasePath(project.id, prev.segment)} className="phase-nav-link prev">
            <IconArrowLeft width={16} height={16} />
            <span>
              <small>Previous work area</small>
              {prev.navLabel}
            </span>
          </Link>
        ) : (
          <span />
        )}
        {next && (
          <Link to={phasePath(project.id, next.segment)} className="phase-nav-link next">
            <span>
              <small>Next work area</small>
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
