/**
 * The project's work areas: the pages where the work of the lifecycle gets done (capture,
 * analysis, validation, design, planning, testing, traceability). They are navigation, not a
 * lifecycle - each one only says which lifecycle stage(s) its work serves. The project's stage is
 * Project.status (see lib/lifecycle).
 *
 * `segment` is the route segment under /projects/:id.
 */
export interface WorkArea {
  key: string;
  label: string;
  navLabel: string;
  segment: string;
  description: string;
  /** Lifecycle stages (Project.status values) this area's work serves. */
  serves: number[];
}

export const WORK_AREAS: WorkArea[] = [
  { key: 'gathering', label: 'Requirements', navLabel: 'Requirements Gathering', segment: 'gathering', description: 'Capture business context, stakeholders, goals and constraints.', serves: [0, 1] },
  { key: 'analysis', label: 'Analysis', navLabel: 'Requirements Analysis', segment: 'analysis', description: 'Turn raw input into structured functional and non-functional requirements.', serves: [2] },
  { key: 'validation', label: 'Validation', navLabel: 'Requirements Validation', segment: 'validation', description: 'Check requirements for ambiguity, conflicts and completeness.', serves: [2] },
  { key: 'design', label: 'Design', navLabel: 'System Design', segment: 'design', description: 'Derive architecture, data and interfaces from approved requirements.', serves: [3] },
  { key: 'development', label: 'Development', navLabel: 'Development', segment: 'development', description: 'Plan implementation tasks and record their completion.', serves: [4, 5] },
  { key: 'testing', label: 'Testing', navLabel: 'Testing', segment: 'testing', description: 'Derive test cases from requirements and record their outcomes.', serves: [6] },
  { key: 'traceability', label: 'Traceability', navLabel: 'Traceability', segment: 'traceability', description: 'Follow every requirement through design, development and tests.', serves: [6] },
];

export function workArea(key: string): WorkArea | undefined {
  return WORK_AREAS.find((a) => a.key === key);
}

export function nextWorkArea(key: string): WorkArea | null {
  const i = WORK_AREAS.findIndex((a) => a.key === key);
  return i >= 0 && i < WORK_AREAS.length - 1 ? WORK_AREAS[i + 1] : null;
}

export function previousWorkArea(key: string): WorkArea | null {
  const i = WORK_AREAS.findIndex((a) => a.key === key);
  return i > 0 ? WORK_AREAS[i - 1] : null;
}

/** The work area whose work serves this lifecycle stage (for "go and do the work" links). */
export function workAreaForStage(status: number): WorkArea {
  return WORK_AREAS.find((a) => a.serves.includes(status)) ?? WORK_AREAS[WORK_AREAS.length - 1];
}

export function phasePath(projectId: string, segment?: string): string {
  return segment ? `/projects/${projectId}/${segment}` : `/projects/${projectId}`;
}
