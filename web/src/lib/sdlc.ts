import { PROJECT_STATUS_LABELS } from '../api/types';

/**
 * The seven SDLC phases AI-REAP walks a project through.
 * `segment` is the route segment under /projects/:id.
 */
export interface SdlcPhase {
  key: string;
  step: number;
  label: string;
  navLabel: string;
  segment: string;
  description: string;
}

export const SDLC_PHASES: SdlcPhase[] = [
  { key: 'gathering', step: 1, label: 'Requirements', navLabel: 'Requirements Gathering', segment: 'gathering', description: 'Capture business context, stakeholders, goals and constraints.' },
  { key: 'analysis', step: 2, label: 'Analysis', navLabel: 'Requirements Analysis', segment: 'analysis', description: 'Turn raw input into structured functional and non-functional requirements.' },
  { key: 'validation', step: 3, label: 'Validation', navLabel: 'Requirements Validation', segment: 'validation', description: 'Check requirements for ambiguity, conflicts and completeness.' },
  { key: 'design', step: 4, label: 'Design', navLabel: 'System Design', segment: 'design', description: 'Derive architecture, data and interfaces from validated requirements.' },
  { key: 'development', step: 5, label: 'Development', navLabel: 'Development', segment: 'development', description: 'Break the design into implementation tasks.' },
  { key: 'testing', step: 6, label: 'Testing', navLabel: 'Testing', segment: 'testing', description: 'Derive test cases from requirements and track them.' },
  { key: 'traceability', step: 7, label: 'Traceability', navLabel: 'Traceability', segment: 'traceability', description: 'Follow every requirement through design, development and tests.' },
];

export type PhaseState = 'completed' | 'current' | 'upcoming' | 'blocked';

export interface PhaseProgress extends SdlcPhase {
  state: PhaseState;
}

export interface SdlcSummary {
  phases: PhaseProgress[];
  /** Number of completed phases. */
  completed: number;
  /** 0-100. */
  percent: number;
  /** Phase currently being worked on, or null once the project is Completed. */
  current: PhaseProgress | null;
  /** True when the project reached the final lifecycle status. */
  finished: boolean;
}

/*
 * The backend records a single lifecycle status per project (Draft → RequirementsGathering →
 * Analysis → Design → Approved → Implementation → Testing → Completed). It has no per-phase state,
 * so phase progress is derived from that status only:
 *
 *   status            completed phases                          current phase
 *   Draft             –                                         Requirements
 *   RequirementsG.    –                                         Requirements
 *   Analysis          Requirements                              Analysis
 *   Design            Requirements, Analysis, Validation        Design
 *   Approved          … + Design                                Development (up next)
 *   Implementation    … + Design                                Development
 *   Testing           … + Development                           Testing
 *   Completed         all seven                                 –
 *
 * Moving from Analysis to Design is the only gate between those stages, so Validation is treated
 * as passed at that point. No phase is ever reported "blocked": the backend has no such state.
 */
const COMPLETED_BY_STATUS = [0, 0, 1, 3, 4, 4, 5, 7];

export function deriveSdlc(status: number): SdlcSummary {
  const idx = Math.min(Math.max(status, 0), COMPLETED_BY_STATUS.length - 1);
  const completed = COMPLETED_BY_STATUS[idx];
  const finished = idx === COMPLETED_BY_STATUS.length - 1;
  const phases: PhaseProgress[] = SDLC_PHASES.map((p, i) => ({
    ...p,
    state: i < completed ? 'completed' : i === completed && !finished ? 'current' : 'upcoming',
  }));
  return {
    phases,
    completed,
    percent: Math.round((completed / SDLC_PHASES.length) * 100),
    current: phases.find((p) => p.state === 'current') ?? null,
    finished,
  };
}

export const PHASE_STATE_LABEL: Record<PhaseState, string> = {
  completed: 'Completed',
  current: 'In progress',
  upcoming: 'Upcoming',
  blocked: 'Blocked',
};

export function nextPhase(key: string): SdlcPhase | null {
  const i = SDLC_PHASES.findIndex((p) => p.key === key);
  return i >= 0 && i < SDLC_PHASES.length - 1 ? SDLC_PHASES[i + 1] : null;
}

export function previousPhase(key: string): SdlcPhase | null {
  const i = SDLC_PHASES.findIndex((p) => p.key === key);
  return i > 0 ? SDLC_PHASES[i - 1] : null;
}

export function phasePath(projectId: string, segment?: string): string {
  return segment ? `/projects/${projectId}/${segment}` : `/projects/${projectId}`;
}

export const PROJECT_STATUS_COUNT = Object.keys(PROJECT_STATUS_LABELS).length;
