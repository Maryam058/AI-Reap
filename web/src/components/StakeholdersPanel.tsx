import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { useAuth } from '../auth/AuthContext';
import { projectsApi } from '../api/projects';
import { ROLES, ROLE_LABELS, type Stakeholder, type UserAccount } from '../api/types';
import { apiErrorText } from '../lib/errors';
import { useToast } from '../context/ToastContext';
import { Avatar } from './ui/Avatar';
import { ConfirmDialog, Dialog } from './ui/Dialog';
import { EmptyState } from './ui/EmptyState';
import { ErrorState } from './ui/ErrorState';
import { TextField } from './ui/TextField';
import { IconEdit, IconPlus, IconTrash, IconUsers } from './icons';

const emptyForm = { name: '', roleInProject: '', contactInfo: '' };
const roleOptions = ROLES.map((r) => ROLE_LABELS[r] ?? r);
const EXTERNAL = '__external__';

/** Stakeholder register for a project: list, add, edit, remove. Uses the existing stakeholder endpoints. */
export function StakeholdersPanel({
  projectId,
  onCountChange,
  onChange,
}: {
  projectId: string;
  onCountChange?: (n: number) => void;
  /** Fires after an add, edit or remove. */
  onChange?: () => void;
}) {
  const { token, hasRole } = useAuth();
  const { toast } = useToast();
  const canManage = hasRole('Administrator') || hasRole('BusinessAnalyst');

  const [items, setItems] = useState<Stakeholder[] | null>(null);
  const [loadError, setLoadError] = useState(false);
  const [editing, setEditing] = useState<Stakeholder | 'new' | null>(null);
  const [form, setForm] = useState(emptyForm);
  const [formError, setFormError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [removing, setRemoving] = useState<Stakeholder | null>(null);
  const [removeBusy, setRemoveBusy] = useState(false);
  const [removeError, setRemoveError] = useState<string | null>(null);
  // The stakeholder is picked from active users of the chosen role, via the BA/Admin stakeholder
  // lookup (not the Administrator-only /api/users). If it fails, the free-text name field is used.
  const [users, setUsers] = useState<UserAccount[] | null>(null);
  const [usersFailed, setUsersFailed] = useState(false);
  // External stakeholders (sponsors, customers...) aren't AI-REAP users: typed by name instead.
  const [external, setExternal] = useState(false);

  const load = useCallback(async () => {
    setLoadError(false);
    try {
      const list = await projectsApi.getStakeholders(projectId, token);
      setItems(list);
      onCountChange?.(list.length);
    } catch {
      setLoadError(true);
    }
  }, [projectId, token, onCountChange]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (!canManage || !editing || users || usersFailed) return;
    projectsApi.getStakeholderCandidates(projectId, token).then(setUsers, () => setUsersFailed(true));
  }, [canManage, editing, users, usersFailed, projectId, token]);

  const usersForRole = (users ?? []).filter((u) => u.isActive && u.role && (ROLE_LABELS[u.role] ?? u.role) === form.roleInProject);
  const pickUsers = canManage && users !== null;
  // Options are keyed by user id (display names aren't unique); an existing stakeholder is matched
  // back to a user by name + email, otherwise its name is kept as its own option.
  const selectedUser = usersForRole.find((u) => u.displayName === form.name && u.email === form.contactInfo);

  const openDialog = (s: Stakeholder | 'new') => {
    setEditing(s);
    setExternal(false);
    setFormError(null);
    setForm(s === 'new' ? emptyForm : { name: s.name, roleInProject: s.roleInProject ?? '', contactInfo: s.contactInfo ?? '' });
  };

  const onSave = async (e: FormEvent) => {
    e.preventDefault();
    if (!form.name.trim()) {
      setFormError('Name is required.');
      return;
    }
    setSaving(true);
    setFormError(null);
    const request = {
      name: form.name,
      roleInProject: form.roleInProject || undefined,
      contactInfo: form.contactInfo || undefined,
    };
    try {
      if (editing && editing !== 'new') await projectsApi.updateStakeholder(projectId, editing.id, request, token);
      else await projectsApi.addStakeholder(projectId, request, token);
      toast(editing === 'new' ? 'Stakeholder added.' : 'Stakeholder updated.');
      setEditing(null);
      onChange?.();
      await load();
    } catch (err) {
      setFormError(apiErrorText(err, 'Could not save the stakeholder. Please try again.'));
    } finally {
      setSaving(false);
    }
  };

  const onRemove = async () => {
    if (!removing) return;
    setRemoveBusy(true);
    setRemoveError(null);
    try {
      await projectsApi.removeStakeholder(projectId, removing.id, token);
      toast(`${removing.name} removed.`);
      setRemoving(null);
      onChange?.();
      await load();
    } catch (err) {
      setRemoveError(apiErrorText(err, 'Could not remove the stakeholder.'));
    } finally {
      setRemoveBusy(false);
    }
  };

  return (
    <section className="panel" aria-labelledby="stakeholders-title">
      <header className="panel-head">
        <div>
          <h2 id="stakeholders-title">Stakeholders</h2>
          <p>
            People who sponsor, use or are affected by this project. Stakeholders are for reference only — to give a
            user access to this project, add them under Members.
          </p>
        </div>
        {canManage && items && items.length > 0 && (
          <button type="button" className="btn btn-primary" onClick={() => openDialog('new')}>
            <IconPlus width={16} height={16} /> Add Stakeholder
          </button>
        )}
      </header>

      {loadError ? (
        <ErrorState title="Unable to load stakeholders" description="We couldn't load the stakeholder list right now." onRetry={load} />
      ) : items === null ? (
        <div className="skeleton skeleton-panel" />
      ) : items.length === 0 ? (
        <EmptyState
          icon={<IconUsers />}
          title="No stakeholders yet"
          description={
            canManage
              ? 'Stakeholders shape requirements. Record who sponsors, uses and is affected by this project so nothing is missed.'
              : 'Nobody has been added to this project yet.'
          }
          action={
            canManage && (
              <button type="button" className="btn btn-primary" onClick={() => openDialog('new')}>
                <IconPlus width={16} height={16} /> Add Stakeholder
              </button>
            )
          }
        />
      ) : (
        <div className="table-scroll">
          <table className="data-table">
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Role</th>
                <th scope="col">Contact</th>
                {canManage && (
                  <th scope="col" className="col-actions">
                    <span className="sr-only">Actions</span>
                  </th>
                )}
              </tr>
            </thead>
            <tbody>
              {items.map((s) => (
                <tr key={s.id}>
                  <td>
                    <div className="cell-person">
                      <Avatar name={s.name} />
                      <strong>{s.name}</strong>
                    </div>
                  </td>
                  <td>{s.roleInProject ? <span className="status-pill tone-info">{s.roleInProject}</span> : <span className="muted">Not set</span>}</td>
                  <td>{s.contactInfo ?? <span className="muted">—</span>}</td>
                  {canManage && (
                    <td className="col-actions">
                      <button type="button" className="icon-btn" onClick={() => openDialog(s)} aria-label={`Edit ${s.name}`} title="Edit">
                        <IconEdit width={15} height={15} />
                      </button>
                      <button
                        type="button"
                        className="icon-btn danger"
                        onClick={() => {
                          setRemoveError(null);
                          setRemoving(s);
                        }}
                        aria-label={`Delete ${s.name}`}
                        title="Delete"
                      >
                        <IconTrash width={15} height={15} />
                      </button>
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {editing && (
        <Dialog
          title={editing === 'new' ? 'Add stakeholder' : 'Edit stakeholder'}
          description="Adding a stakeholder does not grant project access. To give an AI-REAP user access to this project, add them under Project Members."
          onClose={() => !saving && setEditing(null)}
        >
          <form className="dialog-form" onSubmit={onSave} noValidate>
            <label className="field" htmlFor="sh-role">
              <span className="field-label">Role in project</span>
              <select
                id="sh-role"
                className="select-input"
                value={form.roleInProject}
                onChange={(e) => setForm(pickUsers ? { ...form, roleInProject: e.target.value, name: '', contactInfo: '' } : { ...form, roleInProject: e.target.value })}
                data-autofocus
              >
                <option value="">Select a role</option>
                {/* Keep a legacy free-text role selectable when editing an older stakeholder. */}
                {form.roleInProject && !roleOptions.includes(form.roleInProject) && <option value={form.roleInProject}>{form.roleInProject}</option>}
                {roleOptions.map((r) => (
                  <option key={r} value={r}>
                    {r}
                  </option>
                ))}
              </select>
            </label>
            {pickUsers && !external ? (
              <label className="field" htmlFor="sh-name">
                <span className="field-label">Name *</span>
                <select
                  id="sh-name"
                  className="select-input"
                  value={selectedUser?.id ?? (form.name ? `name:${form.name}` : '')}
                  disabled={!form.roleInProject}
                  onChange={(e) => {
                    if (e.target.value === EXTERNAL) {
                      setExternal(true);
                      setForm({ ...form, name: '', contactInfo: '' });
                      return;
                    }
                    const user = usersForRole.find((u) => u.id === e.target.value);
                    if (user) setForm({ ...form, name: user.displayName, contactInfo: user.email });
                    else setForm({ ...form, name: e.target.value.replace(/^name:/, '') });
                  }}
                >
                  <option value="">{!form.roleInProject ? 'Select a role first' : usersForRole.length ? 'Select a user' : 'No users with this role'}</option>
                  {/* Keep the current name selectable when editing a stakeholder who isn't a user of this role. */}
                  {form.name && !selectedUser && <option value={`name:${form.name}`}>{form.name}</option>}
                  {usersForRole.map((u) => (
                    <option key={u.id} value={u.id}>
                      {u.displayName} ({u.email})
                    </option>
                  ))}
                  <option value={EXTERNAL}>External stakeholder (not an AI-REAP user)…</option>
                </select>
                {formError === 'Name is required.' ? (
                  <span className="field-error-text">{formError}</span>
                ) : (
                  selectedUser && <span className="field-hint">Recorded as a stakeholder only; this does not give {selectedUser.displayName} access to the project.</span>
                )}
              </label>
            ) : (
              <>
                <TextField id="sh-name" label="Name *" value={form.name} onChange={(v) => setForm({ ...form, name: v })} placeholder="Full name" error={formError === 'Name is required.' ? formError : undefined} hint={external ? 'External stakeholders are recorded for reference only; they do not get an AI-REAP login or project access.' : undefined} />
                {pickUsers && (
                  <button
                    type="button"
                    className="btn btn-secondary"
                    onClick={() => {
                      setExternal(false);
                      setForm({ ...form, name: '', contactInfo: '' });
                    }}
                  >
                    Pick an AI-REAP user instead
                  </button>
                )}
              </>
            )}
            <TextField id="sh-contact" label="Contact info" value={form.contactInfo} onChange={(v) => setForm({ ...form, contactInfo: v })} placeholder="Email or phone" />
            {formError && formError !== 'Name is required.' && <p className="field-error-text">{formError}</p>}
            <div className="dialog-form-actions">
              <button type="button" className="btn btn-secondary" onClick={() => setEditing(null)} disabled={saving}>
                Cancel
              </button>
              <button type="submit" className="btn btn-primary" disabled={saving}>
                {saving ? 'Saving…' : editing === 'new' ? 'Add Stakeholder' : 'Save Changes'}
              </button>
            </div>
          </form>
        </Dialog>
      )}

      {removing && (
        <ConfirmDialog
          title="Delete stakeholder?"
          description={`${removing.name} will be removed from this project. This action cannot be undone.`}
          confirmLabel="Delete Stakeholder"
          danger
          busy={removeBusy}
          error={removeError}
          onConfirm={onRemove}
          onCancel={() => setRemoving(null)}
        />
      )}
    </section>
  );
}
