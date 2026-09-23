import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { ApiError } from '../api/client';
import { usersApi } from '../api/users';
import { ROLES, ROLE_LABELS, type UserAccount } from '../api/types';
import { PageHeader } from '../components/ui/PageHeader';

// Administrator-only. Hiding this page is a convenience; the API rejects non-administrators
// on every /api/users call, so nothing here relies on the UI for security.

function errorText(err: unknown, fallback: string): string {
  if (!(err instanceof ApiError)) return fallback;
  try {
    const parsed = JSON.parse(err.message) as unknown;
    if (Array.isArray(parsed)) return parsed.join(' ');
    if (typeof parsed === 'string') return parsed;
  } catch {
    // plain-text body
  }
  return err.message || fallback;
}

export function UsersPage() {
  const { token, userId } = useAuth();
  const [users, setUsers] = useState<UserAccount[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const [email, setEmail] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [role, setRole] = useState<string>('BusinessAnalyst');
  const [creating, setCreating] = useState(false);

  const load = useCallback(async () => {
    try {
      setUsers(await usersApi.list(token));
    } catch (err) {
      setError(errorText(err, 'Failed to load users.'));
    }
  }, [token]);

  useEffect(() => {
    void load();
  }, [load]);

  const onCreate = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    setNotice(null);
    setCreating(true);
    try {
      const created = await usersApi.create({ email, displayName, password, role }, token);
      setNotice(`Created ${created.email} as ${ROLE_LABELS[role] ?? role}.`);
      setEmail('');
      setDisplayName('');
      setPassword('');
      await load();
    } catch (err) {
      setError(errorText(err, 'Could not create the user.'));
    } finally {
      setCreating(false);
    }
  };

  const change = async (id: string, action: () => Promise<unknown>, success: string) => {
    setBusyId(id);
    setError(null);
    setNotice(null);
    try {
      await action();
      setNotice(success);
      await load();
    } catch (err) {
      setError(errorText(err, 'Update failed.'));
    } finally {
      setBusyId(null);
    }
  };

  return (
    <>
      <PageHeader
        title="User management"
        subtitle="Create accounts and assign roles. Changes take effect immediately: the affected person is signed out and gets the new role at their next sign-in."
        actions={
          <>
            <Link to="/admin/roles" className="btn btn-secondary">
              Roles &amp; Permissions
            </Link>
            <Link to="/admin/audit" className="btn btn-secondary">
              Audit Logs
            </Link>
          </>
        }
      />

      {error && <p className="error">{error}</p>}
      {notice && <p className="hint">{notice}</p>}

      <form className="card users-create" onSubmit={onCreate}>
        <h2>Add a user</h2>
        <div className="users-create-grid">
          <label>
            Full name
            <input value={displayName} onChange={(e) => setDisplayName(e.target.value)} required />
          </label>
          <label>
            Email
            <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
          </label>
          <label>
            Temporary password
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              minLength={8}
              autoComplete="new-password"
              required
            />
          </label>
          <label>
            Role
            <select value={role} onChange={(e) => setRole(e.target.value)}>
              {ROLES.map((r) => (
                <option key={r} value={r}>
                  {ROLE_LABELS[r]}
                </option>
              ))}
            </select>
          </label>
        </div>
        <button type="submit" disabled={creating}>
          {creating ? 'Creating…' : 'Create user'}
        </button>
      </form>

      <div className="card">
        <h2>Users</h2>
        {!users ? (
          <p>Loading…</p>
        ) : (
          <table className="users-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Email</th>
                <th>Role</th>
                <th>Status</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {users.map((u) => {
                const isSelf = u.id === userId;
                return (
                  <tr key={u.id}>
                    <td>{u.displayName}</td>
                    <td>{u.email}</td>
                    <td>
                      <select
                        aria-label={`Role for ${u.email}`}
                        value={u.role ?? ''}
                        disabled={isSelf || busyId === u.id}
                        onChange={(e) =>
                          change(u.id, () => usersApi.setRole(u.id, e.target.value, token), `Updated role for ${u.email}.`)
                        }
                      >
                        {u.role === null && (
                          <option value="" disabled>
                            No role assigned
                          </option>
                        )}
                        {ROLES.map((r) => (
                          <option key={r} value={r}>
                            {ROLE_LABELS[r]}
                          </option>
                        ))}
                      </select>
                    </td>
                    <td>
                      <span className={`status-pill tone-${u.isActive ? 'success' : 'danger'}`}>
                        {u.isActive ? 'Active' : 'Deactivated'}
                      </span>
                    </td>
                    <td>
                      {isSelf ? (
                        <span className="hint">You</span>
                      ) : (
                        <button
                          type="button"
                          className="secondary"
                          disabled={busyId === u.id}
                          onClick={() =>
                            change(
                              u.id,
                              () => usersApi.setActive(u.id, !u.isActive, token),
                              `${u.isActive ? 'Deactivated' : 'Reactivated'} ${u.email}.`,
                            )
                          }
                        >
                          {u.isActive ? 'Deactivate' : 'Reactivate'}
                        </button>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>
    </>
  );
}
