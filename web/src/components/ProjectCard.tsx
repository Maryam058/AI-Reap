import { Link } from 'react-router-dom';
import type { Project, ProjectLifecycleState } from '../api/types';
import { isGatedStage, lifecycleProgress, LIFECYCLE_STAGES, stageLabel } from '../lib/lifecycle';
import { formatRelativeTime, statusLabel, statusVariant } from '../lib/format';
import { ProgressBar } from './ui/ProgressBar';
import { LifecycleDots } from './LifecycleStepper';
import { ProjectActionsMenu } from './ProjectActionsMenu';
import { IconAlert, IconArrowRight, IconLock } from './icons';

/** A project in the list: its lifecycle stage (Project.status), progress across the 8 stages, and any pending request. */
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
  const gaps = lifecycle?.evidenceGaps.length ?? 0;

  return (
    <article className="project-card">
      <div className="project-card-top">
        <span className={`status-pill tone-${statusVariant(project.status)}`}>{statusLabel(project.status)}</span>
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
        <span className="project-card-label">Next stage</span>
        <strong>
          {progress.finished ? 'Lifecycle completed' : stageLabel(project.status + 1)}
          {!progress.finished && isGatedStage(project.status) && <span className="muted"> · Reviewer gate</span>}
        </strong>
      </div>

      <div className="project-card-progress">
        <div className="progress-meta">
          <span>
            Stage {progress.stageNumber} of {LIFECYCLE_STAGES.length}
          </span>
          <strong>{progress.percent}%</strong>
        </div>
        <ProgressBar percent={progress.percent} />
        <LifecycleDots status={project.status} pending={!!pending} />
        {gaps > 0 && (
          <span className="lc-gap-note">
            <IconAlert width={12} height={12} /> Evidence gap in {gaps} earlier {gaps === 1 ? 'stage' : 'stages'}
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
