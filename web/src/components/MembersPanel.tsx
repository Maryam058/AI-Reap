import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { useAuth } from '../auth/AuthContext';
import { projectsApi } from '../api/projects';
import type { ProjectMember } from '../api/types';
import { apiErrorText } from '../lib/errors';
import { useToast } from '../context/ToastContext';
import { Avatar } from './ui/Avatar';
import { ConfirmDialog, Dialog } from './ui/Dialog';
import { EmptyState } from './ui/EmptyState';
import { ErrorState } from './ui/ErrorState';
import { TextField } from './ui/TextField';
import { IconPlus, IconTrash, IconUser } from './icons';

/** Project membership (access control, §38 DoD): list, add by email, remove. Read access to the
 * list itself is any project member (enforced server-side, same as every other project route);
 * managing it is BA/Admin only, mirroring StakeholdersPanel's gating. */
export function MembersPanel({ projectId }: { projectId: string }) {
  const { token, hasRole, userId } = useAuth();
  const { toast } = useToast();
  const canManage = hasRole('Administrator') || hasRole('BusinessAnalyst');

  const [items, setItems] = useState<ProjectMember[] | null>(null);
  const [loadError, setLoadError] = useState(false);
  const [adding, setAdding] = useState(false);
  const [email, setEmail] = useState('');
  const [formError, setFormError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [removing, setRemoving] = useState<ProjectMember | null>(null);
  const [removeBusy, setRemoveBusy] = useState(false);
  const [removeError, setRemoveError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoadError(false);
    try {
      setItems(await projectsApi.getMembers(projectId, token));
    } catch {
      setLoadError(true);
    }
  }, [projectId, token]);

  useEffect(() => {
    void load();
  }, [load]);

  const onSave = async (e: FormEvent) => {
    e.preventDefault();
    if (!email.trim()) {
      setFormError('Email is required.');
      return;
    }
    setSaving(true);
    setFormError(null);
    try {
      await projectsApi.addMember(projectId, { email: email.trim() }, token);
      toast(`${email.trim()} added to the project.`);
      setAdding(false);
      setEmail('');
      await load();
    } catch (err) {
      setFormError(apiErrorText(err, 'Could not add this member. Check the email is correct and the account exists.'));
    } finally {
      setSaving(false);
    }
  };

  const onRemove = async () => {
    if (!removing) return;
    setRemoveBusy(true);
    setRemoveError(null);
    try {
      await projectsApi.removeMember(projectId, removing.userId, token);
      toast(`${removing.displayName} removed from the project.`);
      setRemoving(null);
      await load();
    } catch (err) {
      setRemoveError(apiErrorText(err, 'Could not remove this member.'));
    } finally {
      setRemoveBusy(false);
    }
  };

  return (
    <section className="panel" aria-labelledby="members-title">
      <header className="panel-head">
        <div>
          <h2 id="members-title">Members</h2>
          <p>Who has access to this project. Everyone else with a role is refused, even Administrators' bypass aside.</p>
        </div>
        {canManage && items && items.length > 0 && (
          <button type="button" className="btn btn-primary" onClick={() => { setFormError(null); setEmail(''); setAdding(true); }}>
            <IconPlus width={16} height={16} /> Add Member
          </button>
        )}
      </header>

      {loadError ? (
        <ErrorState title="Unable to load members" description="We couldn't load the member list right now." onRetry={load} />
      ) : items === null ? (
        <div className="skeleton skeleton-panel" />
      ) : items.length === 0 ? (
        <EmptyState
          icon={<IconUser />}
          title="No members yet"
          description={canManage ? 'Add teammates by email so they can access this project.' : 'Nobody has been added to this project yet.'}
          action={
            canManage && (
              <button type="button" className="btn btn-primary" onClick={() => { setFormError(null); setEmail(''); setAdding(true); }}>
                <IconPlus width={16} height={16} /> Add Member
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
                <th scope="col">Email</th>
                <th scope="col">Added</th>
                {canManage && (
                  <th scope="col" className="col-actions">
                    <span className="sr-only">Actions</span>
                  </th>
                )}
              </tr>
            </thead>
            <tbody>
              {items.map((m) => {
                const isSelf = m.userId === userId;
                return (
                  <tr key={m.id}>
                    <td>
                      <div className="cell-person">
                        <Avatar name={m.displayName} />
                        <strong>{m.displayName}</strong>
                        {isSelf && <span className="hint"> (you)</span>}
                      </div>
                    </td>
                    <td>{m.email}</td>
                    <td>{new Date(m.addedAt).toLocaleDateString()}</td>
                    {canManage && (
                      <td className="col-actions">
                        <button
                          type="button"
                          className="icon-btn danger"
                          onClick={() => {
                            setRemoveError(null);
                            setRemoving(m);
                          }}
                          aria-label={`Remove ${m.displayName}`}
                          title="Remove"
                        >
                          <IconTrash width={15} height={15} />
                        </button>
                      </td>
                    )}
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {adding && (
        <Dialog title="Add member" description="They must already have an account; an administrator creates accounts under User management." onClose={() => !saving && setAdding(false)}>
          <form className="dialog-form" onSubmit={onSave} noValidate>
            <TextField
              id="member-email"
              label="Email *"
              type="email"
              value={email}
              onChange={setEmail}
              placeholder="name@company.com"
              error={formError === 'Email is required.' ? formError : undefined}
              data-autofocus
            />
            {formError && formError !== 'Email is required.' && <p className="field-error-text">{formError}</p>}
            <div className="dialog-form-actions">
              <button type="button" className="btn btn-secondary" onClick={() => setAdding(false)} disabled={saving}>
                Cancel
              </button>
              <button type="submit" className="btn btn-primary" disabled={saving}>
                {saving ? 'Adding…' : 'Add Member'}
              </button>
            </div>
          </form>
        </Dialog>
      )}

      {removing && (
        <ConfirmDialog
          title="Remove member?"
          description={`${removing.displayName} will lose access to this project. This action cannot be undone.`}
          confirmLabel="Remove Member"
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
