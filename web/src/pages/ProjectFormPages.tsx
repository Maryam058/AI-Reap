import { useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { projectsApi } from '../api/projects';
import { useProject } from '../context/ProjectContext';
import { useToast } from '../context/ToastContext';
import { apiErrorText } from '../lib/errors';
import { PageHeader } from '../components/ui/PageHeader';
import { EmptyState } from '../components/ui/EmptyState';
import { EMPTY_VALUES, ProjectEditor, ProjectWizard, toRequest, valuesFromProject, type FieldKey, type ProjectValues } from '../components/ProjectForm';
import { IconCheckCircle, IconDocument, IconInfo, IconLock } from '../components/icons';

function useCanManage() {
  const { hasRole } = useAuth();
  return hasRole('Administrator') || hasRole('BusinessAnalyst');
}

function NoAccess({ what }: { what: string }) {
  return (
    <EmptyState
      icon={<IconLock width={20} height={20} />}
      title="You don't have access to this page"
      description={`Only administrators and business analysts can ${what}. Ask one of them for help.`}
      action={
        <Link to="/projects" className="btn btn-secondary">
          Back to projects
        </Link>
      }
    />
  );
}

export function NewProjectPage() {
  const { token } = useAuth();
  const canManage = useCanManage();
  const navigate = useNavigate();
  const { toast } = useToast();
  const [values, setValues] = useState<ProjectValues>(EMPTY_VALUES);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!canManage) return <NoAccess what="create projects" />;

  const onSubmit = async () => {
    setBusy(true);
    setError(null);
    try {
      const created = await projectsApi.create(toRequest(values), token);
      toast(`${created.name} created.`);
      navigate(`/projects/${created.id}`);
    } catch (err) {
      setError(apiErrorText(err, 'Could not create the project. Please try again.'));
      setBusy(false);
    }
  };

  return (
    <>
      <PageHeader eyebrow="New project" title="Create a new project" subtitle="Describe the project once. AI-REAP carries this context through all nine stages, from gathering requirements to the final review." />
      <div className="pf-create">
        <ProjectWizard values={values} onChange={(k: FieldKey, v: string) => setValues((s) => ({ ...s, [k]: v }))} onSubmit={onSubmit} onCancel={() => navigate('/projects')} busy={busy} error={error} />
        <aside className="pf-guide" aria-label="Before you start">
          <h2>
            <IconInfo width={16} height={16} aria-hidden="true" /> Before you start
          </h2>
          <ul>
            <li>
              <IconCheckCircle width={15} height={15} aria-hidden="true" />
              <span>
                Only the <strong>project name</strong> is required. Everything else is optional and can be completed later in the Gathering stage.
              </span>
            </li>
            <li>
              <IconCheckCircle width={15} height={15} aria-hidden="true" />
              <span>The more context you give, the better the AI’s analysis will be.</span>
            </li>
            <li>
              <IconDocument width={15} height={15} aria-hidden="true" />
              <span>
                You will upload your requirements document (SRS) <strong>after</strong> creating the project. Supported formats: PDF, Word (.docx) and plain text (.txt).
              </span>
            </li>
          </ul>
        </aside>
      </div>
    </>
  );
}

/** Shared save logic for the edit page and the Requirements Gathering context tab. */
export function useProjectEditing() {
  const { token } = useAuth();
  const { toast } = useToast();
  const { project, setProject } = useProject();
  const saved = valuesFromProject(project);
  const [values, setValues] = useState<ProjectValues>(saved);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Re-sync when the project changes from elsewhere (e.g. stage update), unless mid-edit.
  useEffect(() => {
    setValues(valuesFromProject(project));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project.id, project.updatedAt]);

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      const updated = await projectsApi.update(project.id, toRequest(values), token);
      setProject(updated);
      toast('Project changes saved.');
    } catch (err) {
      setError(apiErrorText(err, 'Could not save your changes. Please try again.'));
    } finally {
      setBusy(false);
    }
  };

  return {
    values,
    saved,
    busy,
    error,
    save,
    change: (k: FieldKey, v: string) => setValues((s) => ({ ...s, [k]: v })),
    reset: () => setValues(valuesFromProject(project)),
  };
}

export function EditProjectPage() {
  const canManage = useCanManage();
  const navigate = useNavigate();
  const { project } = useProject();
  const editing = useProjectEditing();

  if (!canManage) return <NoAccess what="edit projects" />;

  return (
    <>
      <PageHeader eyebrow={project.name} title="Edit Project" subtitle="Update project information and configuration." />
      <ProjectEditor
        values={editing.values}
        saved={editing.saved}
        onChange={editing.change}
        onSave={editing.save}
        busy={editing.busy}
        error={editing.error}
        saveLabel="Save Changes"
        onCancel={() => navigate(`/projects/${project.id}`)}
      />
    </>
  );
}
