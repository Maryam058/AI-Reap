import { Link } from 'react-router-dom';
import type { Project } from '../api/types';
import { formatRelativeTime, stageProgress, statusLabel, statusVariant } from '../lib/format';
import { ProgressBar } from './ui/ProgressBar';

export function ProjectCard({ project }: { project: Project }) {
  const percent = stageProgress(project.status);

  return (
    <Link to={`/projects/${project.id}`} className="project-card">
      <div className="project-card-top">
        <span className="project-card-name">{project.name}</span>
        <span className={`status-pill tone-${statusVariant(project.status)}`}>{statusLabel(project.status)}</span>
      </div>
      <p className="project-card-desc">{project.description || 'No description yet.'}</p>
      <div className="project-card-progress">
        <ProgressBar percent={percent} />
        <div className="progress-meta">
          <span>SDLC progress</span>
          <span>{percent}%</span>
        </div>
      </div>
      <div className="project-card-footer">
        <span>Updated {formatRelativeTime(project.updatedAt)}</span>
      </div>
    </Link>
  );
}
