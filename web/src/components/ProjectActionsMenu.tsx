import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { projectsApi } from '../api/projects';
import type { Project } from '../api/types';
import { apiErrorText } from '../lib/errors';
import { useToast } from '../context/ToastContext';
import { ConfirmDialog } from './ui/Dialog';
import { Menu, type MenuItem } from './ui/Menu';
import { IconArrowRight, IconCheckCircle, IconEdit, IconFolder, IconTrash } from './icons';

/**
 * The "⋯" action menu for a project: Open, Edit, the project's lifecycle, the SDLC Review, and (Administrator only)
 * Delete. Stage changes are not made from here: they happen in the lifecycle panel on the overview,
 * where the backend's exit checks, stage ownership and Reviewer gates are shown.
 */
export function ProjectActionsMenu({
  project,
  showOpen = true,
  label,
  onDeleted,
}: {
  project: Project;
  /** Kept for callers that refresh after changes; the menu itself no longer changes the project. */
  onUpdated?: (p: Project) => void;
  /** Called after a successful delete. Without it, the menu navigates back to the project list. */
  onDeleted?: (projectId: string) => void;
  showOpen?: boolean;
  label?: string;
}) {
  const { token, hasRole } = useAuth();
  const { toast } = useToast();
  const navigate = useNavigate();
  const canEdit = hasRole('Administrator') || hasRole('BusinessAnalyst');
  const canDelete = hasRole('Administrator');

  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const items: MenuItem[] = [];
  if (showOpen) items.push({ key: 'open', label: 'Open project', icon: <IconFolder width={15} height={15} />, to: `/projects/${project.id}` });
  if (canEdit) items.push({ key: 'edit', label: 'Edit project', icon: <IconEdit width={15} height={15} />, to: `/projects/${project.id}/edit` });
  items.push({ key: 'lifecycle', label: 'Lifecycle stage & approvals', icon: <IconArrowRight width={15} height={15} />, to: `/projects/${project.id}#lifecycle` });
  items.push({ key: 'review', label: 'SDLC Review', icon: <IconCheckCircle width={15} height={15} />, to: `/projects/${project.id}/review` });
  if (canDelete) {
    items.push({
      key: 'delete',
      label: 'Delete project',
      icon: <IconTrash width={15} height={15} />,
      danger: true,
      onSelect: () => {
        setError(null);
        setConfirming(true);
      },
    });
  }

  const confirmDelete = async () => {
    setBusy(true);
    setError(null);
    try {
      await projectsApi.remove(project.id, token);
      setConfirming(false);
      toast(`Deleted “${project.name}”.`, 'success');
      if (onDeleted) onDeleted(project.id);
      else navigate('/projects');
    } catch (err) {
      setError(apiErrorText(err, 'Could not delete the project.'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <Menu items={items} label={label ?? `Actions for ${project.name}`} />
      {confirming && (
        <ConfirmDialog
          title="Delete project?"
          description={
            <>
              <strong>{project.name}</strong> and all of its requirements, designs, tasks, tests, documents, agent runs,
              lifecycle history and members will be permanently deleted. This cannot be undone.
            </>
          }
          confirmLabel="Delete project"
          danger
          busy={busy}
          error={error}
          onConfirm={confirmDelete}
          onCancel={() => !busy && setConfirming(false)}
        />
      )}
    </>
  );
}
