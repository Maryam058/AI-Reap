import { useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import type { Project } from '../../api/types';
import { useStageSignals } from '../../context/StageSignalsContext';
import { stageLabel } from '../../lib/lifecycle';
import { nextStage, previousStage, STAGE_COUNT, STAGE_STATUS_LABEL, stageByKey, stageStatus, stageStatusReason, type StageKey, type StageStatus } from '../../lib/stages';
import { phasePath } from '../../lib/workAreas';
import { Term } from '../ui/Term';
import { IconArrowLeft, IconArrowRight, IconBulb, IconChevronDown } from '../icons';
import { StageIcon } from './StageIcon';

const INTRO_KEY = 'aireap.stage-intro-hidden';

function loadHidden(): string[] {
  try {
    return JSON.parse(localStorage.getItem(INTRO_KEY) ?? '[]') as string[];
  } catch {
    return [];
  }
}

export function StageStatusPill({ status }: { status: StageStatus }) {
  return (
    <span className={`stage-pill st-${status}`}>
      <span className="stage-pill-dot" aria-hidden="true" />
      {STAGE_STATUS_LABEL[status]}
    </span>
  );
}

/**
 * The top of every stage page: stage number and position, icon, name, a plain-language purpose,
 * the stage's derived status, and a "What happens in this stage?" panel that can be folded away
 * (remembered per stage, in this browser only).
 */
export function StageHeader({
  stageKey,
  project,
  badge,
  actions,
}: {
  stageKey: StageKey;
  project: Project;
  /** Extra context under the title (e.g. how this page relates to the lifecycle status). */
  badge?: ReactNode;
  actions?: ReactNode;
}) {
  const stage = stageByKey(stageKey)!;
  const signals = useStageSignals();
  const status = stageStatus(stage, project.status, signals);
  const [hidden, setHidden] = useState<string[]>(loadHidden);
  const open = !hidden.includes(stageKey);
  const introId = `stage-intro-${stageKey}`;

  const toggle = () =>
    setHidden((prev) => {
      const next = prev.includes(stageKey) ? prev.filter((k) => k !== stageKey) : [...prev, stageKey];
      try {
        localStorage.setItem(INTRO_KEY, JSON.stringify(next));
      } catch {
        // best effort
      }
      return next;
    });

  return (
    <>
      <div className="stage-header">
        <div className="stage-header-main">
          <span className={`stage-header-icon st-${status}`} aria-hidden="true">
            <StageIcon stage={stageKey} width={22} height={22} />
          </span>
          <div className="stage-header-text">
            <div className="stage-header-eyebrow">
              Stage {stage.number} of {STAGE_COUNT}
              {stage.kind === 'tool' && <span className="stage-header-kind">Supporting tool</span>}
            </div>
            <h1>{stage.name}</h1>
            <p className="stage-header-summary">{stage.summary}</p>
            <div className="stage-header-meta">
              <StageStatusPill status={status} />
              {badge}
            </div>
          </div>
        </div>
        {actions && <div className="page-header-actions">{actions}</div>}
      </div>

      <section className={`stage-intro${open ? ' open' : ''}`} aria-labelledby={`${introId}-title`}>
        <h2 className="stage-intro-heading" id={`${introId}-title`}>
          <button type="button" className="stage-intro-toggle" aria-expanded={open} aria-controls={introId} onClick={toggle}>
            <IconBulb width={16} height={16} aria-hidden="true" />
            <span>What happens in this stage?</span>
            <IconChevronDown width={15} height={15} className="stage-intro-chevron" aria-hidden="true" />
          </button>
        </h2>
        <div id={introId} className="stage-intro-body" hidden={!open}>
          <p className="stage-intro-text">{stage.explainer}</p>
          <div className="stage-intro-cols">
            <div>
              <h3>What you can do here</h3>
              <ul className="stage-intro-list">
                {stage.activities.map((a) => (
                  <li key={a}>{a}</li>
                ))}
              </ul>
            </div>
            <div>
              <h3>Terms used in this stage</h3>
              <p className="stage-intro-terms">
                {stage.terms.map((t) => (
                  <Term key={t} id={t} chip />
                ))}
              </p>
              <p className="stage-intro-status">
                <strong>Status:</strong> {stageStatusReason(stage, project.status, signals)}
                {stage.serves.length > 0 && (
                  <>
                    {' '}
                    Lifecycle status: <Term id="lifecycleStatus" label={stageLabel(project.status)} />.
                  </>
                )}
              </p>
            </div>
          </div>
        </div>
      </section>
    </>
  );
}

/** Bottom of a stage page: where to go next, without bypassing anything — every stage is a plain link. */
export function StageNextStep({ stageKey, project }: { stageKey: StageKey; project: Project }) {
  const prev = previousStage(stageKey);
  const next = nextStage(stageKey);
  return (
    <nav className="stage-next" aria-label="Next step">
      {prev ? (
        <Link to={phasePath(project.id, prev.segment)} className="stage-next-prev">
          <IconArrowLeft width={15} height={15} aria-hidden="true" />
          <span>
            <small>Previous stage</small>
            {prev.number}. {prev.name}
          </span>
        </Link>
      ) : (
        <Link to={phasePath(project.id)} className="stage-next-prev">
          <IconArrowLeft width={15} height={15} aria-hidden="true" />
          <span>
            <small>Back to</small>
            Project overview
          </span>
        </Link>
      )}
      {next ? (
        <Link to={phasePath(project.id, next.segment)} className="stage-next-card">
          <span className="stage-next-icon" aria-hidden="true">
            <StageIcon stage={next.key} width={18} height={18} />
          </span>
          <span className="stage-next-text">
            <small>
              Next step · Stage {next.number} of {STAGE_COUNT}
            </small>
            <strong>{next.name}</strong>
            <span>{next.summary}</span>
          </span>
          <IconArrowRight width={18} height={18} aria-hidden="true" />
        </Link>
      ) : (
        <Link to={phasePath(project.id)} className="stage-next-card">
          <span className="stage-next-text">
            <small>You have reached the last stage</small>
            <strong>Back to the project overview</strong>
            <span>See progress across all nine stages and what to do next.</span>
          </span>
          <IconArrowRight width={18} height={18} aria-hidden="true" />
        </Link>
      )}
    </nav>
  );
}
