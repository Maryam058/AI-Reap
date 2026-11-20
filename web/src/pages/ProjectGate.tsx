import { Link, Outlet, useLocation } from 'react-router-dom';
import type { Project } from '../api/types';
import { useProjectContext } from '../context/ProjectContext';
import { StageNavigator } from '../components/stage/StageNavigator';
import { ErrorState } from '../components/ui/ErrorState';
import { PanelSkeleton } from '../components/ui/Skeletons';

/** Guards every /projects/:id/* page: renders a skeleton while loading and a friendly error if it fails. */
export function ProjectGate() {
  const { project, error, reload } = useProjectContext();

  if (project) return <ProjectWorkspace project={project} />;

  if (error === 'not-found') {
    return (
      <ErrorState
        title="Project not found"
        description="This project doesn't exist or you no longer have access to it."
        action={
          <Link to="/projects" className="btn btn-secondary">
            Back to projects
          </Link>
        }
      />
    );
  }
  if (error) {
    return <ErrorState title="Unable to load project" description="We couldn't load this project right now." onRetry={reload} />;
  }
  return (
    <div className="gate-loading" aria-busy="true" aria-label="Loading project">
      <div className="skeleton skeleton-title" />
      <div className="skeleton skeleton-line" style={{ width: '40%' }} />
      <PanelSkeleton />
      <PanelSkeleton />
    </div>
  );
}

/**
 * The frame around every page of an open project: shows the nine-stage navigator above the stage
 * pages, with statuses from the lifecycle state ProjectProvider loads once for the whole shell. The
 * overview has its own, larger stage map, and the edit form is not a stage, so neither repeats it.
 */
function ProjectWorkspace({ project }: { project: Project }) {
  const { pathname } = useLocation();
  const { lifecycle } = useProjectContext();
  const segment = pathname.split('/')[3] ?? '';
  const showNavigator = segment !== '' && segment !== 'edit';
  return (
    <>
      {showNavigator && <StageNavigator project={project} signals={lifecycle} />}
      <Outlet />
    </>
  );
}
