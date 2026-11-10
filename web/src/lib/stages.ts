import { LAST_STAGE, stageLabel, stageState } from './lifecycle.ts';
import { WORK_AREAS } from './workAreas.ts';

/**
 * The nine workspace stages a user moves through in a project. They are presentation only: seven
 * are the existing work areas (lib/workAreas), plus the AI Copilot and the SDLC Review pages.
 *
 * A stage's status is never stored or invented. It is derived from:
 *   - Project.status (the formal lifecycle status, lib/lifecycle) via the lifecycle statuses each
 *     work area already declares it serves, and
 *   - the project's real lifecycle state (a pending Reviewer request, evidence gaps), when loaded.
 * The AI Copilot and the Review have no completion of their own, so they are shown as tools that
 * are always available (the Review becomes the next step once the lifecycle is completed).
 */
export type StageKey = 'gathering' | 'analysis' | 'validation' | 'design' | 'development' | 'testing' | 'traceability' | 'copilot' | 'review';

export type StageStatus = 'not-started' | 'in-progress' | 'awaiting' | 'attention' | 'completed' | 'available' | 'ready';

export interface WorkspaceStage {
  key: StageKey;
  /** 1-based position in the nine-stage workflow. */
  number: number;
  name: string;
  /** Route segment under /projects/:id. */
  segment: string;
  /** One short line, shown on cards and in the navigator. */
  summary: string;
  /** "What happens in this stage?" — plain language, no jargon. */
  explainer: string;
  /** What a user typically does here. */
  activities: string[];
  /** Glossary terms (lib/glossary) worth explaining on this stage. */
  terms: string[];
  /** Lifecycle statuses (Project.status) the stage's work serves; empty for tools. */
  serves: number[];
  kind: 'work' | 'tool';
}

const serves = (key: string) => WORK_AREAS.find((a) => a.key === key)?.serves ?? [];

export const STAGES: WorkspaceStage[] = [
  {
    key: 'gathering',
    number: 1,
    name: 'Gathering',
    segment: 'gathering',
    summary: 'Describe the project and upload your requirements.',
    explainer:
      'Upload your Software Requirements Specification (SRS) or paste meeting notes, and record who the project is for. Everything AI-REAP produces later is built from what you add here.',
    activities: ['Fill in the project context: problem, goals and scope', 'Record stakeholders and add team members', 'Upload an SRS (.pdf, .docx or .txt) or paste requirement notes'],
    terms: ['srs', 'stakeholder', 'requirementSource'],
    serves: serves('gathering'),
    kind: 'work',
  },
  {
    key: 'analysis',
    number: 2,
    name: 'Analysis',
    segment: 'analysis',
    summary: 'AI turns your input into structured requirements.',
    explainer:
      'AI reads your requirement sources and organises them into functional and non-functional requirements, business rules, user stories and acceptance criteria. You review and approve what it produces.',
    activities: ['Run the AI analysis on a requirement source', 'Generate requirements, user stories and business rules', 'Approve, edit or reject each generated item'],
    terms: ['fr', 'nfr', 'businessRule', 'userStory', 'acceptanceCriteria'],
    serves: serves('analysis'),
    kind: 'work',
  },
  {
    key: 'validation',
    number: 3,
    name: 'Validation',
    segment: 'validation',
    summary: 'Resolve questions, conflicts and quality issues.',
    explainer:
      'Check that the requirements are clear and consistent. Answer the questions AI raised about missing or ambiguous information, and resolve any requirements that contradict each other.',
    activities: ['Answer open clarification questions', 'Review potential conflicts between requirements', 'Run a quality check and act on its findings'],
    terms: ['clarification', 'conflict'],
    serves: serves('validation'),
    kind: 'work',
  },
  {
    key: 'design',
    number: 4,
    name: 'Design',
    segment: 'design',
    summary: 'Turn approved requirements into a technical design.',
    explainer: 'Generate the solution design, the data the system stores and the interfaces (APIs) it exposes, all derived from the approved requirements. A developer reviews the design.',
    activities: ['Generate the solution design', 'Generate data entities and API specifications', 'Review and approve the design artifacts'],
    terms: ['designArtifact', 'dataEntity', 'apiSpec'],
    serves: serves('design'),
    kind: 'work',
  },
  {
    key: 'development',
    number: 5,
    name: 'Development',
    segment: 'development',
    summary: 'Plan and track the implementation tasks.',
    explainer: 'Break the approved requirements and design into implementation tasks, then record their progress as developers complete them.',
    activities: ['Generate implementation tasks from the requirements', 'Review each task and its linked requirement', 'Record work as tasks are completed'],
    terms: ['task'],
    serves: serves('development'),
    kind: 'work',
  },
  {
    key: 'testing',
    number: 6,
    name: 'Testing',
    segment: 'testing',
    summary: 'Create test cases and record the results.',
    explainer: 'Generate test cases from the requirements, run them and record whether each one passed. This shows that what was built does what was asked for.',
    activities: ['Generate test cases for each requirement', 'Record pass / fail results', 'Check how many requirements are covered by tests'],
    terms: ['testCase', 'coverage'],
    serves: serves('testing'),
    kind: 'work',
  },
  {
    key: 'traceability',
    number: 7,
    name: 'Traceability',
    segment: 'traceability',
    summary: 'See how every requirement connects to the work.',
    explainer: 'Follow each requirement through its design, tasks and tests, so you can see at a glance whether anything has been missed.',
    activities: ['Find requirements without a design, task or test', 'Open any linked item to check it'],
    terms: ['traceability'],
    serves: serves('traceability'),
    kind: 'work',
  },
  {
    key: 'copilot',
    number: 8,
    name: 'AI Copilot',
    segment: 'copilot',
    summary: 'Ask questions about your project.',
    explainer:
      'Ask questions about your project in plain language — for example what is missing or what changed recently. Answers are based on this project’s requirements and documents. You can also run and review the AI agents here.',
    activities: ['Ask about gaps, coverage or recent changes', 'Run and review the AI agent pipeline', 'See the audit trail of AI activity'],
    terms: ['agentPipeline'],
    serves: [],
    kind: 'tool',
  },
  {
    key: 'review',
    number: 9,
    name: 'Review',
    segment: 'review',
    summary: 'Review what each stage produced and who approved it.',
    explainer: 'A read-only summary of the whole project: what every lifecycle stage produced, who approved it, and how requirements are traced to tests.',
    activities: ['Check each stage’s artifacts and approvals', 'Follow the project timeline', 'Confirm readiness before sign-off'],
    terms: ['lifecycleStatus', 'reviewerApproval'],
    serves: [],
    kind: 'tool',
  },
];

export const STAGE_COUNT = STAGES.length;

export function stageByKey(key: string | null | undefined): WorkspaceStage | undefined {
  return STAGES.find((s) => s.key === key);
}

export function stageBySegment(segment: string | null | undefined): WorkspaceStage | undefined {
  return STAGES.find((s) => s.segment === segment);
}

export function nextStage(key: StageKey): WorkspaceStage | null {
  const i = STAGES.findIndex((s) => s.key === key);
  return i >= 0 && i < STAGES.length - 1 ? STAGES[i + 1] : null;
}

export function previousStage(key: StageKey): WorkspaceStage | null {
  const i = STAGES.findIndex((s) => s.key === key);
  return i > 0 ? STAGES[i - 1] : null;
}

/** The part of the lifecycle state the stage statuses use (absent until it has loaded). */
export interface StageSignals {
  pendingTransition?: { fromStatus?: number | null; toStatus: number } | null;
  evidenceGaps?: { stage: number }[];
}

export const STAGE_STATUS_LABEL: Record<StageStatus, string> = {
  'not-started': 'Not started',
  'in-progress': 'In progress',
  awaiting: 'Awaiting approval',
  attention: 'Needs attention',
  completed: 'Completed',
  available: 'Available anytime',
  ready: 'Ready to review',
};

/** Status of one stage for a project, from Project.status and (optionally) its lifecycle state. */
export function stageStatus(stage: WorkspaceStage, projectStatus: number, signals?: StageSignals | null): StageStatus {
  const finished = projectStatus >= LAST_STAGE;
  if (stage.kind === 'tool') return stage.key === 'review' && finished ? 'ready' : 'available';
  const states = stage.serves.map((s) => stageState(s, projectStatus));
  const gaps = signals?.evidenceGaps ?? [];
  if (states.every((s) => s === 'completed')) {
    return stage.serves.some((s) => gaps.some((g) => g.stage === s)) ? 'attention' : 'completed';
  }
  if (states.includes('current')) return signals?.pendingTransition ? 'awaiting' : 'in-progress';
  return 'not-started';
}

/** Why a stage has the status it has, in one sentence. */
export function stageStatusReason(stage: WorkspaceStage, projectStatus: number, signals?: StageSignals | null): string {
  const status = stageStatus(stage, projectStatus, signals);
  const milestones = stage.serves.map(stageLabel).join(' & ');
  switch (status) {
    case 'completed':
      return `The project has passed the ${milestones} lifecycle status.`;
    case 'attention':
      return `The project passed ${milestones}, but some of its exit checks no longer hold.`;
    case 'awaiting':
      return `Waiting for a Reviewer to approve the move to ${stageLabel(signals?.pendingTransition?.toStatus)}.`;
    case 'in-progress':
      return `The project is currently in ${stageLabel(projectStatus)}, which this stage's work serves.`;
    case 'not-started':
      return `Starts once the project reaches ${milestones}. You can look around at any time.`;
    case 'ready':
      return 'The lifecycle is completed. Review what every stage produced.';
    default:
      return stage.key === 'review' ? 'A read-only summary you can open at any time.' : 'You can use this at any stage of the project.';
  }
}

/**
 * The stage the project's current work happens in: the first work stage that serves the current
 * lifecycle status, or the Review once the lifecycle is completed.
 */
export function currentStage(projectStatus: number): WorkspaceStage {
  if (projectStatus >= LAST_STAGE) return STAGES[STAGES.length - 1];
  return STAGES.find((s) => s.serves.includes(projectStatus)) ?? STAGES[0];
}

/** Every work stage serving the current lifecycle status (e.g. Analysis and Validation both serve Analysis). */
export function activeStages(projectStatus: number): WorkspaceStage[] {
  if (projectStatus >= LAST_STAGE) return [STAGES[STAGES.length - 1]];
  return STAGES.filter((s) => s.serves.includes(projectStatus));
}

/** Short "Now in Analysis & Validation" text for cards and headers. */
export function activeStagesLabel(projectStatus: number): string {
  return activeStages(projectStatus)
    .map((s) => s.name)
    .join(' & ');
}

/** How many of the seven work stages are completed (tools excluded). Derived, never stored. */
export function completedWorkStages(projectStatus: number): { done: number; total: number } {
  const work = STAGES.filter((s) => s.kind === 'work');
  return { done: work.filter((s) => stageStatus(s, projectStatus) === 'completed').length, total: work.length };
}
