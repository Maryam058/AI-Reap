import { useAuth } from '../auth/AuthContext';
import type { Project } from '../api/types';
import { Menu, type MenuItem } from './ui/Menu';
import { IconArrowRight, IconEdit, IconFolder } from './icons';

/**
 * The "⋯" action menu for a project: Open, Edit, and the project's lifecycle. Stage changes are not
 * made from here: they happen in the lifecycle panel on the overview, where the backend's exit
 * checks, stage ownership and Reviewer gates are shown. Delete is intentionally absent — the API
 * has no project-delete endpoint.
 */
export function ProjectActionsMenu({
  project,
  showOpen = true,
  label,
}: {
  project: Project;
  /** Kept for callers that refresh after changes; the menu itself no longer changes the project. */
  onUpdated?: (p: Project) => void;
  showOpen?: boolean;
  label?: string;
}) {
  const { hasRole } = useAuth();
  const canEdit = hasRole('Administrator') || hasRole('BusinessAnalyst');

  const items: MenuItem[] = [];
  if (showOpen) items.push({ key: 'open', label: 'Open project', icon: <IconFolder width={15} height={15} />, to: `/projects/${project.id}` });
  if (canEdit) items.push({ key: 'edit', label: 'Edit project', icon: <IconEdit width={15} height={15} />, to: `/projects/${project.id}/edit` });
  items.push({ key: 'lifecycle', label: 'Lifecycle stage & approvals', icon: <IconArrowRight width={15} height={15} />, to: `/projects/${project.id}#lifecycle` });

  return <Menu items={items} label={label ?? `Actions for ${project.name}`} />;
}
