import { ARTIFACT_TYPE_VALUES, type BlockerGroup, type BlockingItem, type CheckKind } from '../api/types.ts';
import { phasePath } from './workAreas.ts';
import { roleLabel } from './lifecycle.ts';

/**
 * Routes for exit-check guidance (ExitCheckGuidanceService). The backend names an area and an
 * optional section anchor; this maps them to the existing work screens. Guidance only navigates -
 * the work itself happens on those screens, under their own permission checks.
 */

const T = ARTIFACT_TYPE_VALUES;

/** The work-area segment and section anchor where an artifact of this type is listed. */
const ARTIFACT_PLACE: Record<number, { segment: string; section: string }> = {
  [T.FunctionalRequirement]: { segment: 'analysis', section: 'sec-functional' },
  [T.NonFunctionalRequirement]: { segment: 'analysis', section: 'sec-nonfunctional' },
  [T.BusinessRule]: { segment: 'analysis', section: 'sec-rules' },
  [T.UserStory]: { segment: 'analysis', section: 'sec-stories' },
  [T.AcceptanceCriterion]: { segment: 'analysis', section: 'sec-criteria' },
  [T.ClarificationQuestion]: { segment: 'validation', section: 'sec-clarifications' },
  [T.DesignArtifact]: { segment: 'design', section: 'sec-architecture' },
  [T.ApiSpecification]: { segment: 'design', section: 'sec-api' },
  [T.DataEntity]: { segment: 'design', section: 'sec-data' },
  [T.ImplementationTask]: { segment: 'development', section: 'sec-tasks' },
  [T.TestCase]: { segment: 'testing', section: 'sec-tests' },
};

export function sectionForArtifactType(type: number): string | null {
  return ARTIFACT_PLACE[type]?.section ?? null;
}

const AREA_ROUTE: Record<string, { segment: string; tab?: string } | undefined> = {
  context: { segment: 'gathering' },
  members: { segment: 'gathering', tab: 'members' },
  stakeholders: { segment: 'gathering', tab: 'stakeholders' },
  sources: { segment: 'gathering', tab: 'sources' },
  analysis: { segment: 'analysis' },
  validation: { segment: 'validation' },
  design: { segment: 'design' },
  development: { segment: 'development' },
  testing: { segment: 'testing' },
  traceability: { segment: 'traceability' },
};

const AREA_LABEL: Record<string, string> = {
  context: 'Project context',
  members: 'Members',
  stakeholders: 'Stakeholders',
  sources: 'Requirement sources',
  analysis: 'Analysis',
  validation: 'Validation',
  design: 'Design',
  development: 'Development',
  testing: 'Testing',
  traceability: 'Traceability',
};

function withQuery(path: string, params: Record<string, string | null | undefined>): string {
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v) q.set(k, v);
  const s = q.toString();
  return s ? `${path}?${s}` : path;
}

/** The route for a blocker group's destination, or null for work done on the lifecycle panel itself (or an unknown area). */
export function destinationPath(projectId: string, destination: BlockerGroup['destination']): string | null {
  const route = AREA_ROUTE[destination.area];
  if (!route) return null;
  return withQuery(phasePath(projectId, route.segment), { tab: route.tab, section: destination.section });
}

export function destinationLabel(destination: BlockerGroup['destination']): string | null {
  return AREA_LABEL[destination.area] ?? null;
}

/** A link straight to one blocking artifact: its work area, its requirement source, the card itself. */
export function itemPath(projectId: string, item: BlockingItem): string {
  const place = ARTIFACT_PLACE[item.artifactType];
  if (!place) return phasePath(projectId, 'traceability');
  // Clarification questions are listed as a block, not as focusable cards.
  if (item.artifactType === T.ClarificationQuestion) {
    return withQuery(phasePath(projectId, place.segment), { source: item.requirementSourceId, section: place.section });
  }
  return withQuery(phasePath(projectId, place.segment), { source: item.requirementSourceId, focus: item.artifactId });
}

export const KIND_LABEL: Record<CheckKind, string> = {
  action: 'Action',
  approval: 'Approval',
  signoff: 'Sign-off',
  automatic: 'Automatic',
};

export function kindLabel(kind: string): string {
  return KIND_LABEL[kind as CheckKind] ?? kind;
}

/** "Developer", "Reviewer or Business Analyst", "Reviewer, Business Analyst or Administrator". */
export function rolesText(roles: string[]): string {
  const labels = roles.map(roleLabel);
  if (labels.length <= 1) return labels[0] ?? '';
  return `${labels.slice(0, -1).join(', ')} or ${labels[labels.length - 1]}`;
}

/** The roles to name in "Needs …": an Administrator is left out when another role is listed (they act only as a stand-in). */
export function primaryRoles(roles: string[]): string[] {
  const named = roles.filter((r) => r !== 'Administrator');
  return named.length > 0 ? named : roles;
}
