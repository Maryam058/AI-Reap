import { Link } from 'react-router-dom';
import type { Project } from '../api/types';
import { deriveSdlc } from '../lib/sdlc';
import { formatRelativeTime, statusLabel, statusVariant } from '../lib/format';
import { ProgressBar } from './ui/ProgressBar';
import { SdlcDots } from './SdlcStepper';
import { ProjectActionsMenu } from './ProjectActionsMenu';
import { IconArrowRight } from './icons';

export function ProjectCard({ project, onUpdated }: { project: Project; onUpdated: (p: Project) => void }) {
  const sdlc = deriveSdlc(project.status);

  return (
    <article className="project-card">
      <div className="project-card-top">
        <span className={`status-pill tone-${statusVariant(project.status)}`}>{statusLabel(project.status)}</span>
        <ProjectActionsMenu project={project} onUpdated={onUpdated} />
      </div>

      <h3 className="project-card-name">
        <Link to={`/projects/${project.id}`}>{project.name}</Link>
      </h3>
      <p className="project-card-desc">{project.description || 'No description yet.'}</p>

      <div className="project-card-phase">
        <span className="project-card-label">Current phase</span>
        <strong>{sdlc.current?.navLabel ?? 'All phases complete'}</strong>
      </div>

      <div className="project-card-progress">
        <div className="progress-meta">
          <span>SDLC progress</span>
          <strong>{sdlc.percent}%</strong>
        </div>
        <ProgressBar percent={sdlc.percent} />
        <SdlcDots phases={sdlc.phases} />
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
