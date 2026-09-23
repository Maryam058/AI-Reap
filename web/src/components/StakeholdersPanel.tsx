import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { useAuth } from '../auth/AuthContext';
import { projectsApi } from '../api/projects';
import type { Stakeholder } from '../api/types';
import { apiErrorText } from '../lib/errors';
import { useToast } from '../context/ToastContext';
import { Avatar } from './ui/Avatar';
import { ConfirmDialog, Dialog } from './ui/Dialog';
import { EmptyState } from './ui/EmptyState';
import { ErrorState } from './ui/ErrorState';
import { TextField } from './ui/TextField';
import { IconEdit, IconPlus, IconTrash, IconUsers } from './icons';

const emptyForm = { name: '', roleInProject: '', contactInfo: '' };

/** Stakeholder register for a project: list, add, edit, remove. Uses the existing stakeholder endpoints. */
export function StakeholdersPanel({ projectId, onCountChange }: { projectId: string; onCountChange?: (n: number) => void }) {
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

  const openDialog = (s: Stakeholder | 'new') => {
    setEditing(s);
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
          <p>People who sponsor, use or are affected by this project.</p>
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
          description="Stakeholders are recorded against this project only."
          onClose={() => !saving && setEditing(null)}
        >
          <form className="dialog-form" onSubmit={onSave} noValidate>
            <TextField id="sh-name" label="Name *" value={form.name} onChange={(v) => setForm({ ...form, name: v })} placeholder="Full name" error={formError === 'Name is required.' ? formError : undefined} data-autofocus />
            <TextField id="sh-role" label="Role in project" value={form.roleInProject} onChange={(v) => setForm({ ...form, roleInProject: v })} placeholder="e.g. Sponsor, End user" />
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
