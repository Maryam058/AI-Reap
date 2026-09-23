import { useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { projectsApi } from '../api/projects';
import type { Project } from '../api/types';
import { statusLabel } from '../lib/format';
import { apiErrorText } from '../lib/errors';
import { PROJECT_STATUS_COUNT } from '../lib/sdlc';
import { useToast } from '../context/ToastContext';
import { ConfirmDialog } from './ui/Dialog';
import { Menu, type MenuItem } from './ui/Menu';
import { IconArrowRight, IconEdit, IconFolder } from './icons';

/**
 * The "⋯" action menu for a project: Open, Edit, and Update stage (advance to the next lifecycle
 * status). Delete is intentionally absent — the API has no project-delete endpoint.
 */
export function ProjectActionsMenu({
  project,
  onUpdated,
  showOpen = true,
  label,
}: {
  project: Project;
  onUpdated: (p: Project) => void;
  showOpen?: boolean;
  label?: string;
}) {
  const { token, hasRole } = useAuth();
  const { toast } = useToast();
  const canEdit = hasRole('Administrator') || hasRole('BusinessAnalyst');
  const canAdvance = (canEdit || hasRole('Reviewer')) && project.status < PROJECT_STATUS_COUNT - 1;
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const items: MenuItem[] = [];
  if (showOpen) items.push({ key: 'open', label: 'Open project', icon: <IconFolder width={15} height={15} />, to: `/projects/${project.id}` });
  if (canEdit) items.push({ key: 'edit', label: 'Edit project', icon: <IconEdit width={15} height={15} />, to: `/projects/${project.id}/edit` });
  if (canAdvance) {
    items.push({
      key: 'advance',
      label: `Update stage → ${statusLabel(project.status + 1)}`,
      icon: <IconArrowRight width={15} height={15} />,
      onSelect: () => {
        setError(null);
        setConfirming(true);
      },
    });
  }
  if (items.length === 0) return null;

  const advance = async () => {
    setBusy(true);
    setError(null);
    try {
      const updated = await projectsApi.updateStatus(project.id, project.status + 1, token);
      onUpdated(updated);
      toast(`${project.name} moved to ${statusLabel(updated.status)}.`);
      setConfirming(false);
    } catch (err) {
      setError(apiErrorText(err, 'Could not update the stage. Please try again.'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <Menu items={items} label={label ?? `Actions for ${project.name}`} />
      {confirming && (
        <ConfirmDialog
          title="Update project stage?"
          description={
            <>
              <strong>{project.name}</strong> will move from <strong>{statusLabel(project.status)}</strong> to <strong>{statusLabel(project.status + 1)}</strong>. Stages advance one step at a time.
            </>
          }
          confirmLabel={`Move to ${statusLabel(project.status + 1)}`}
          busy={busy}
          error={error}
          onConfirm={advance}
          onCancel={() => setConfirming(false)}
        />
      )}
    </>
  );
}
