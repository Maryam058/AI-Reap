import { Link } from 'react-router-dom';
import type { Project, ProjectLifecycleState } from '../api/types';
import { isGatedStage, lifecycleProgress, stageLabel } from '../lib/lifecycle';
import { activeStagesLabel, APPROVED_PROGRESS_LABEL, currentStage, outstandingWorkNote, STAGE_COUNT } from '../lib/stages';
import { formatRelativeTime, statusLabel, statusVariant } from '../lib/format';
import { ProgressBar } from './ui/ProgressBar';
import { StageDots } from './stage/StageNavigator';
import { StageIcon } from './stage/StageIcon';
import { ProjectActionsMenu } from './ProjectActionsMenu';
import { IconAlert, IconArrowRight, IconLock } from './icons';

/** A project in the list: its lifecycle status (Project.status), where its work is among the nine stages, progress, and any pending request. */
export function ProjectCard({
  project,
  lifecycle,
  onUpdated,
  onDeleted,
}: {
  project: Project;
  lifecycle?: ProjectLifecycleState;
  onUpdated: (p: Project) => void;
  onDeleted?: (projectId: string) => void;
}) {
  const progress = lifecycleProgress(project.status);
  const pending = lifecycle?.pendingTransition ?? null;
  const outstanding = outstandingWorkNote(project.status, lifecycle);
  const focus = currentStage(project.status);

  return (
    <article className="project-card">
      <div className="project-card-top">
        {pending && (
          <span className="status-pill tone-warning" title={`Waiting for a Reviewer to approve ${stageLabel(pending.fromStatus)} → ${stageLabel(pending.toStatus)}`}>
            <IconLock width={11} height={11} /> Awaiting review
          </span>
        )}
        <ProjectActionsMenu project={project} onUpdated={onUpdated} onDeleted={onDeleted} />
      </div>

      <h3 className="project-card-name">
        <Link to={`/projects/${project.id}`}>{project.name}</Link>
      </h3>
      <p className="project-card-desc">{project.description || 'No description yet.'}</p>

      <div className="project-card-phase">
        <span className="project-card-label">Current stage</span>
        <span className="project-card-stage">
          <span className={`status-pill tone-${statusVariant(project.status)}`}>{statusLabel(project.status)}</span>
        </span>
        <span className="project-card-next">
          {progress.finished ? (
            outstanding ? 'Lifecycle approved · gaps reopened since' : 'Lifecycle completed'
          ) : (
            <>
              Next: <strong>{stageLabel(project.status + 1)}</strong>
              {isGatedStage(project.status) && (
                <span className="project-card-gate" title="A Reviewer must approve before the project can leave this stage">
                  <IconLock width={11} height={11} aria-hidden="true" /> Needs Reviewer approval
                </span>
              )}
            </>
          )}
        </span>
      </div>

      <div className="project-card-work">
        <span className="project-card-work-icon" aria-hidden="true">
          <StageIcon stage={focus.key} width={15} height={15} />
        </span>
        <span>
          {progress.finished ? 'Ready for' : 'Work now in'} <strong>{progress.finished ? focus.name : activeStagesLabel(project.status)}</strong>
          <small>
            {' '}
            · stage {focus.number} of {STAGE_COUNT}
          </small>
        </span>
      </div>

      <div className="project-card-progress">
        <div className="progress-meta">
          <span>{APPROVED_PROGRESS_LABEL}</span>
          <strong>{progress.percent}%</strong>
        </div>
        <ProgressBar percent={progress.percent} />
        <StageDots status={project.status} signals={lifecycle} />
        {outstanding && (
          <span className="lc-gap-note">
            <IconAlert width={12} height={12} /> {progress.finished ? 'Approved, but ' : ''}
            {outstanding}
          </span>
        )}
      </div>

      <div className="project-card-footer">
        <span>Updated {formatRelativeTime(project.updatedAt)}</span>
        <Link to={`/projects/${project.id}`} className="btn btn-secondary btn-sm">
          Open Project <IconArrowRight width={14} height={14} />
        </Link>
      </div>
    </article>
  );
}
