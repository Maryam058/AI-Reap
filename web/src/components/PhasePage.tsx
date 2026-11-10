import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { useProject } from '../context/ProjectContext';
import { stageLabel, stageState } from '../lib/lifecycle';
import { phasePath, workArea } from '../lib/workAreas';
import type { StageKey } from '../lib/stages';
import { StageHeader, StageNextStep } from './stage/StageHeader';
import { IconSparkles } from './icons';

/**
 * Shared frame for every work-area stage (gathering, analysis, validation, design, development,
 * testing, traceability): the stage header (number, purpose, derived status), a plain-language
 * "What happens in this stage?" panel, how the page relates to the project's lifecycle status, the
 * content, and the next step. Stages are navigation; the lifecycle status is Project.status and
 * only moves on the project overview.
 */
export function PhasePage({ phaseKey, actions, children }: { phaseKey: StageKey; actions?: ReactNode; children: ReactNode }) {
  const { project } = useProject();
  const area = workArea(phaseKey);
  const serves = area?.serves ?? [];
  // How this area's stages relate to where the project actually is.
  const states = serves.map((s) => stageState(s, project.status));
  const relation = states.includes('current') ? 'current' : states.every((s) => s === 'completed') ? 'completed' : 'upcoming';
  const servesText = serves.map(stageLabel).join(' & ');
  const relationText =
    relation === 'current'
      ? 'This page is for the current stage'
      : relation === 'completed'
        ? `This page supports ${servesText}, already passed`
        : `This page supports ${servesText}, a later stage`;

  return (
    <>
      <StageHeader
        stageKey={phaseKey}
        project={project}
        badge={
          serves.length > 0 && (
            <span className={`phase-badge ${relation}`} title={`This page's work serves the ${servesText} lifecycle status${serves.length > 1 ? 'es' : ''}`}>
              Project is in <strong>{stageLabel(project.status)}</strong> · {relationText}
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

      <div className="phase-body">{children}</div>

      <StageNextStep stageKey={phaseKey} project={project} />
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
