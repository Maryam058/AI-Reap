import type { ReactNode } from 'react';
import { useAuth } from '../auth/AuthContext';
import { useRoleGuard } from '../hooks/useRoleGuard';
import { roleGuard, roleRequiredMessage } from '../lib/roleGuard';
import { IconAlert, IconX } from './icons';

export interface AdministratorOption {
  /** What an Administrator can do instead, and why it is not the required role's evidence. */
  explanation: string;
  /** Only where the backend deliberately lets an Administrator act. */
  action?: { label: string; onClick: () => void; busy?: boolean };
}

/**
 * "⚠ Developer role required": what role the action needs, what the user is signed in as, and that
 * they must switch accounts. For an Administrator it adds `administrator`, kept visibly apart from
 * the required role's evidence (an Administrator record, or a lifecycle override).
 */
export function RoleRequiredAlert({ requiredRole, administrator, onDismiss }: { requiredRole: string; administrator?: AdministratorOption; onDismiss: () => void }) {
  const { roles } = useAuth();
  const message = roleRequiredMessage(requiredRole, roles);
  const showAdmin = administrator && roleGuard(requiredRole, roles).kind === 'administrator';
  return (
    <div className="alert alert-warning role-required" role="alert">
      <IconAlert width={16} height={16} />
      <span className="role-required-text">
        <strong>{message.title}</strong>
        <span>{message.body}</span>
        {showAdmin && (
          <span className="role-required-admin">
            {administrator.explanation}
            {administrator.action && (
              <button type="button" className="btn btn-secondary btn-sm" disabled={administrator.action.busy} onClick={administrator.action.onClick}>
                {administrator.action.label}
              </button>
            )}
          </span>
        )}
      </span>
      <button type="button" className="role-required-close" aria-label="Dismiss" onClick={onDismiss}>
        <IconX width={14} height={14} />
      </button>
    </div>
  );
}

/**
 * Buttons for an action that belongs to another role, shown to a user who cannot take it (neither
 * that role nor Administrator) so the action is visible and clicking explains who has to do it.
 * Nothing is sent to the backend.
 */
export function RoleRequiredActions({ requiredRole, actions }: { requiredRole: string; actions: { label: ReactNode; className: string }[] }) {
  const guard = useRoleGuard(requiredRole);
  return (
    <>
      {actions.map((a, i) => (
        <button key={i} type="button" className={a.className} onClick={() => guard.attempt(() => undefined)}>
          {a.label}
        </button>
      ))}
      {guard.raised && <RoleRequiredAlert requiredRole={requiredRole} onDismiss={guard.dismiss} />}
    </>
  );
}
