// Imported with its extension so `node --test` can load this module directly (tests/roleGuard.test.ts).
import { ROLE_LABELS } from '../api/types.ts';

/**
 * Role-specific lifecycle actions: the backend accepts the action, or counts it as lifecycle
 * evidence, only when it is made under one particular role. Mirrors, for guidance only:
 *  - ProjectLifecyclePolicy: each stage's owner moves the project on; a Reviewer decides a gate;
 *  - LifecycleEvidenceService: only a Developer's task record, QA's test record and a Developer's
 *    design review count as evidence.
 * The API stays the authority; this only stops the wrong user from acting by mistake.
 */
export type RoleGuardDecision =
  /** The user holds the required role; the backend records the action under it. */
  | { kind: 'allowed' }
  /** Administrator without the required role: acts only as an Administrator (never as that role's evidence) or by override. */
  | { kind: 'administrator' }
  /** Neither: the user has to switch to an account with the required role. */
  | { kind: 'blocked' };

export function roleGuard(requiredRole: string, userRoles: readonly string[]): RoleGuardDecision {
  if (userRoles.includes(requiredRole)) return { kind: 'allowed' };
  if (userRoles.includes('Administrator')) return { kind: 'administrator' };
  return { kind: 'blocked' };
}

const ROLE_NAME: Record<string, string> = { Reviewer: 'Reviewer' };
const ROLE_NOUN: Record<string, string> = {
  BusinessAnalyst: 'a Business Analyst',
  Developer: 'a Developer',
  QA: 'a QA user',
  Reviewer: 'a Reviewer',
};

/** A role's name in running text ("Reviewer", not the "Reviewer / Manager" list label). */
export const roleName = (role: string) => ROLE_NAME[role] ?? ROLE_LABELS[role] ?? role;
const roleNoun = (role: string) => ROLE_NOUN[role] ?? `a ${roleName(role)}`;

function joinRoles(roles: readonly string[]): string {
  const names = roles.map(roleName);
  return names.length <= 1 ? (names[0] ?? '') : `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`;
}

export interface RoleRequiredMessage {
  title: string;
  body: string;
}

export function roleRequiredMessage(requiredRole: string, userRoles: readonly string[]): RoleRequiredMessage {
  const name = roleName(requiredRole);
  const current = userRoles.length > 0 ? `You are currently signed in as ${joinRoles(userRoles)}.` : 'You are currently signed in without a project role.';
  return {
    title: `${name} role required`,
    body: `This action must be performed by ${roleNoun(requiredRole)}. ${current} Please switch to a ${name} account to continue.`,
  };
}
