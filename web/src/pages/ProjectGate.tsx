import { Link, Outlet } from 'react-router-dom';
import { useProjectContext } from '../context/ProjectContext';
import { ErrorState } from '../components/ui/ErrorState';
import { PanelSkeleton } from '../components/ui/Skeletons';

/** Guards every /projects/:id/* page: renders a skeleton while loading and a friendly error if it fails. */
export function ProjectGate() {
  const { project, error, reload } = useProjectContext();

  if (project) return <Outlet />;

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
