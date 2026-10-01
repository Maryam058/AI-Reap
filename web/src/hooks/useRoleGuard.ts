import { useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { roleGuard, type RoleGuardDecision } from '../lib/roleGuard';

/**
 * One guard for every role-specific lifecycle action (see lib/roleGuard). `attempt(action)` runs the
 * action when the user holds the required role; otherwise it raises the RoleRequiredAlert, which the
 * caller renders next to the button. The backend still decides; this only prevents confusion.
 */
export function useRoleGuard(requiredRole: string) {
  const { roles } = useAuth();
  const decision: RoleGuardDecision = roleGuard(requiredRole, roles);
  const [raised, setRaised] = useState(false);
  return {
    decision,
    raised,
    attempt: (action: () => void) => (decision.kind === 'allowed' ? action() : setRaised(true)),
    dismiss: () => setRaised(false),
  };
}
