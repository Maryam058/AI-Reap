// Mirrors AiReap.Application DTOs (AuthDtos, ProjectDtos, Ai/AiDemoRequest).
// Kept as a single hand-written file for now; revisit generating this from Swagger
// once the API surface stabilizes past Phase 0.

export type ProjectStatus =
  | 'Draft'
  | 'RequirementsGathering'
  | 'Analysis'
  | 'Design'
  | 'Approved'
  | 'Implementation'
  | 'Testing'
  | 'Completed';

// The backend serializes the ProjectStatus enum as its numeric value by default.
export const PROJECT_STATUS_LABELS: Record<number, ProjectStatus> = {
  0: 'Draft',
  1: 'RequirementsGathering',
  2: 'Analysis',
  3: 'Design',
  4: 'Approved',
  5: 'Implementation',
  6: 'Testing',
  7: 'Completed',
};

export interface AuthResponse {
  token: string;
  userId: string;
  email: string;
  displayName: string;
  roles: string[];
}

// Public sign-up never carries a role — accounts start with none and an Administrator assigns one.
export interface RegisterRequest {
  email: string;
  password: string;
  displayName: string;
}

// Administrator user management (GET/POST /api/users ...). `role` is null until one is assigned.
export interface UserAccount {
  id: string;
  email: string;
  displayName: string;
  role: string | null;
  isActive: boolean;
}

export interface CreateUserRequest {
  email: string;
  displayName: string;
  password: string;
  role: string;
}

export const ROLE_LABELS: Record<string, string> = {
  Administrator: 'Administrator',
  BusinessAnalyst: 'Business Analyst',
  Developer: 'Developer',
  QA: 'QA',
  Reviewer: 'Reviewer / Manager',
};

export interface LoginRequest {
  email: string;
  password: string;
}

export interface Project {
  id: string;
  name: string;
  description?: string | null;
  businessProblem?: string | null;
  objectives?: string | null;
  scope?: string | null;
  domain?: string | null;
  targetUsers?: string | null;
  technologyPreferences?: string | null;
  constraints?: string | null;
  expectedTimeline?: string | null;
  status: number;
  createdAt: string;
  updatedAt: string;
}

export interface CreateProjectRequest {
  name: string;
  description?: string;
  businessProblem?: string;
  objectives?: string;
  scope?: string;
  domain?: string;
  targetUsers?: string;
  technologyPreferences?: string;
  constraints?: string;
  expectedTimeline?: string;
}

export const ROLES = ['Administrator', 'BusinessAnalyst', 'Developer', 'QA', 'Reviewer'] as const;
export type Role = (typeof ROLES)[number];

// GET /api/roles/matrix - reflects the API's real [Authorize(Roles = ...)] attributes
// (RolesController), so this never needs hand-updating when a role rule changes.
export interface Capability {
  label: string;
  roles: string[];
}

export interface CapabilityGroup {
  group: string;
  items: Capability[];
}

export type UpdateProjectRequest = CreateProjectRequest;

// §5/§29 — project stakeholders (REAP-023).
export interface Stakeholder {
  id: string;
  projectId: string;
  name: string;
  roleInProject?: string | null;
  contactInfo?: string | null;
}

export interface CreateStakeholderRequest {
  name: string;
  roleInProject?: string;
  contactInfo?: string;
}

// §38 DoD — project membership (access control, not a stakeholder/contact record).
export interface ProjectMember {
  id: string;
  projectId: string;
  userId: string;
  email: string;
  displayName: string;
  addedByUserId: string;
  addedAt: string;
  /** The account's role (Administrator, BusinessAnalyst, …); null for a deleted account. */
  role: string | null;
  /** False when an Administrator has deactivated the account (or it was deleted). */
  isActive: boolean;
}

export interface AddProjectMemberRequest {
  email: string;
}

// ---- Requirement pipeline (§6-§13) ----------------------------------------------------

export const ARTIFACT_TYPE_LABELS: Record<number, string> = {
  0: 'FunctionalRequirement',
  1: 'NonFunctionalRequirement',
  2: 'BusinessRule',
  3: 'UserStory',
  4: 'AcceptanceCriterion',
  5: 'ClarificationQuestion',
  6: 'DesignArtifact',
  7: 'ApiSpecification',
  8: 'DataEntity',
  9: 'ImplementationTask',
  10: 'TestCase',
  11: 'BusinessObjective',
};

export const ARTIFACT_TYPE_VALUES = {
  FunctionalRequirement: 0,
  NonFunctionalRequirement: 1,
  BusinessRule: 2,
  UserStory: 3,
  AcceptanceCriterion: 4,
  ClarificationQuestion: 5,
  DesignArtifact: 6,
  ApiSpecification: 7,
  DataEntity: 8,
  ImplementationTask: 9,
  TestCase: 10,
  BusinessObjective: 11,
} as const;

export const ARTIFACT_STATUS = {
  AiGenerated: 0,
  Draft: 1,
  UnderReview: 2,
  Approved: 3,
  Rejected: 4,
  Implemented: 5,
  Verified: 6,
} as const;

export const ARTIFACT_STATUS_LABELS: Record<number, string> = {
  0: 'AI Generated',
  1: 'Draft',
  2: 'Under Review',
  3: 'Approved',
  4: 'Rejected',
  5: 'Implemented',
  6: 'Verified',
};

export const ARTIFACT_PRIORITY_LABELS: Record<number, string> = {
  0: 'Low',
  1: 'Medium',
  2: 'High',
  3: 'Critical',
};

export type RequirementSourceType = 0 | 1 | 2; // Manual | Paste | FileUpload

export interface RequirementSource {
  id: string;
  projectId: string;
  sourceType: RequirementSourceType;
  rawText: string;
  originalFileName?: string | null;
  createdAt: string;
}

export interface CreateRequirementSourceRequest {
  sourceType: RequirementSourceType;
  rawText: string;
  originalFileName?: string;
}

export interface ArtifactSummary {
  id: string;
  projectId: string;
  artifactType: number;
  code: string;
  title: string;
  priority: number | null;
  status: number;
  origin: number; // 0 = Ai, 1 = Human
  requirementSourceId: string | null;
  data: Record<string, unknown>;
  currentVersion: number;
  createdAt: string;
  updatedAt: string;
  // §24 — the version a reviewer last approved (null until first approval).
  approvedVersion?: number | null;
  // §22 — upstream changes this artifact hasn't been reviewed against yet.
  openImpactNoticeCount?: number;
  // Who created or generated the artifact (e.g. an NFR's author may not confirm its target).
  createdByUserId?: string | null;
}

export interface ArtifactVersion {
  versionNumber: number;
  data: Record<string, unknown>;
  changedByUserId: string;
  changedAt: string;
  reason?: string | null;
  origin: number;
  // Null for versions recorded before full snapshots existed.
  title?: string | null;
  priority?: number | null;
  status?: number | null;
}

export interface ArtifactReview {
  id: string;
  reviewerUserId: string;
  decision: number; // 0 Approved | 1 Rejected | 2 CommentOnly
  comment?: string | null;
  reviewedAt: string;
  versionNumber?: number | null;
  reviewerRole?: string | null; // role acted under, e.g. Developer for a design review; null on older rows
}

export interface ArtifactRelationship {
  relatedArtifactId: string;
  relatedArtifactCode: string;
  relatedArtifactTitle: string;
  relatedArtifactType: number;
  relationshipType: number;
  isOutgoing: boolean;
}

export interface ClarificationQuestionData {
  question: string;
  reason?: string | null;
  clarificationStatus: 'Open' | 'Answered' | 'Resolved' | 'NotApplicable';
  answer?: string | null;
}

export interface FunctionalRequirementData {
  actor: string;
  preconditions?: string | null;
  inputs?: string | null;
  processing?: string | null;
  expectedResult?: string | null;
  dependencies: string[];
  // §26 — project document excerpts this requirement is grounded in, e.g. "notes.pdf#3".
  sourceReferences?: string[];
}

export interface NonFunctionalRequirementData {
  category: string;
  description?: string | null;
  targetValue?: string | null;
  assumptionStatus?: 'confirmed' | 'proposed_assumption' | null;
  // Server-set when a BA/Reviewer confirms the target (POST /api/artifacts/{id}/nfr-confirmation).
  confirmedByUserId?: string | null;
  confirmedAt?: string | null;
}

export interface UserStoryData {
  persona: string;
  valueStatement: string;
}

export interface AcceptanceCriterionData {
  given: string;
  when: string;
  then: string;
  kind: 'positive' | 'negative' | 'boundary';
}

export interface BusinessRuleData {
  statement: string;
}

export interface DesignArtifactData {
  architectureOverview: string;
  modules: string[];
  integrationPoints?: string | null;
  authentication?: string | null;
  backgroundProcessing?: string | null;
  caching?: string | null;
  logging?: string | null;
  deploymentConsiderations?: string | null;
}

export interface DataEntityField {
  name: string;
  dataType: string;
  required: boolean;
}

export interface DataEntityData {
  fields: DataEntityField[];
  keys: string[];
  relationships?: string | null;
  indexes: string[];
}

export interface ApiSpecificationData {
  purpose: string;
  method: string;
  route: string;
  request?: string | null;
  response?: string | null;
  validation?: string | null;
  authorization?: string | null;
}

export interface ImplementationTaskData {
  description: string;
  taskType: string;
  dependencies: string[];
  suggestedRole?: string | null;
  estimate?: string | null;
}

export interface TestCaseData {
  preconditions?: string | null;
  steps: string[];
  expectedResult: string;
  testKind: 'positive' | 'negative' | 'boundary' | 'permission' | 'validation';
}

export interface QualityFinding {
  artifactId: string;
  artifactCode: string;
  artifactTitle: string;
  issue: string;
  recommendation: string;
}

export const RELATIONSHIP_TYPE_LABELS: Record<number, string> = {
  0: 'derived from',
  1: 'implements',
  2: 'tested by',
  3: 'depends on',
  4: 'conflicts with',
  5: 'duplicate of',
  6: 'linked rule',
};

export interface TraceRef {
  id: string;
  code: string;
  title: string;
}

export interface TraceabilityRow {
  functionalRequirementId: string;
  functionalRequirementCode: string;
  functionalRequirementTitle: string;
  businessObjectives: TraceRef[];
  businessRules: TraceRef[];
  userStories: TraceRef[];
  acceptanceCriteria: TraceRef[];
  designArtifacts: TraceRef[];
  dataEntities: TraceRef[];
  apiSpecifications: TraceRef[];
  implementationTasks: TraceRef[];
  testCases: TraceRef[];
}

export interface ImpactedArtifact {
  id: string;
  code: string;
  title: string;
  artifactType: string;
  status: string;
  relationshipType: string;
  path?: string;
}

export interface BusinessObjective {
  id: string;
  code: string;
  title: string;
  requirements: TraceRef[];
}

// §22 — raised automatically on each downstream artifact when approved content changes.
export interface ImpactNotice {
  id: string;
  sourceArtifactId: string;
  sourceArtifactCode: string;
  sourceArtifactTitle: string;
  sourceVersion: number;
  affectedArtifactId: string;
  affectedArtifactCode: string;
  affectedArtifactTitle: string;
  affectedArtifactType: number;
  affectedArtifactStatus: number;
  path: string;
  createdByUserId: string;
  createdAt: string;
  acknowledgedByUserId?: string | null;
  acknowledgedAt?: string | null;
  acknowledgementNote?: string | null;
}

export interface ImpactAnalysisResult {
  artifactId: string;
  artifactCode: string;
  artifactTitle: string;
  approvedOrLaterDownstream: ImpactedArtifact[];
  otherDownstream: ImpactedArtifact[];
}

export interface AiExecution {
  id: string;
  operationType: string;
  userId: string;
  timestamp: string;
  model: string;
  promptTemplateVersion?: string | null;
  inputReference?: string | null;
  outputJson: string;
  accepted?: boolean | null;
  producedArtifactId?: string | null;
}

export interface ConflictFinding {
  artifactAId: string;
  artifactACode: string;
  artifactATitle: string;
  artifactBId: string;
  artifactBCode: string;
  artifactBTitle: string;
  relationshipType: number; // 4 = ConflictsWith, 5 = DuplicateOf
  reason: string;
}

export interface AnalyzeRequirementResult {
  actors: string[];
  capabilities: string[];
  dataElements: string[];
  notes: string[];
  clarificationQuestions: ArtifactSummary[];
}

export interface GenerateRequirementsResult {
  functionalRequirements: ArtifactSummary[];
  nonFunctionalRequirements: ArtifactSummary[];
}

export interface ClarificationAnswerRequest {
  answer: string;
  notApplicable: boolean;
}

export interface UpdateArtifactStatusRequest {
  status: number;
  comment?: string;
}

// §23 — human edit; the backend replaces `data` wholesale (never a partial patch of it), so
// callers always send back the full typed payload for the artifact's type.
export interface UpdateArtifactRequest {
  title?: string;
  priority?: number | null;
  data?: Record<string, unknown>;
  reason?: string;
}

export interface RecentChangeItem {
  code: string;
  title: string;
  artifactType: string;
  status: string;
  updatedAt: string;
}

export interface ProjectDashboard {
  functionalRequirementCount: number;
  nonFunctionalRequirementCount: number;
  businessRuleCount: number;
  userStoryCount: number;
  acceptanceCriterionCount: number;
  openClarificationCount: number;
  approvedCount: number;
  pendingReviewCount: number;
  rejectedCount: number;
  conflictCount: number;
  recentChanges: RecentChangeItem[];
  openImpactNoticeCount?: number;
  /** §28 FRs with at least one TestedBy test case; coverage = this / functionalRequirementCount. Project-wide. */
  testedFunctionalRequirementCount?: number;
  /** §28 FRs + NFRs in AiGenerated, Draft or UnderReview. Unlike pendingReviewCount, requirements only. */
  requirementsNeedingReviewCount?: number;
}

// ---- Project lifecycle (§5) ------------------------------------------------------------
// Project.status is the lifecycle stage (0 Draft … 7 Completed). Everything below comes from
// ProjectLifecycleService (Application/Lifecycle); the UI never computes evidence or permissions.

/** One exit check of a lifecycle stage: what must hold before the project may leave it. */
export interface LifecycleCheck {
  label: string;
  passed: boolean;
  detail: string;
  /** "How to meet this" for a failing check, for the current viewer (absent on passed checks and in history). */
  guidance?: CheckGuidance | null;
}

/** CheckKinds: what meets a check or a blocker group. */
export type CheckKind = 'action' | 'approval' | 'signoff' | 'automatic';

/** CheckAreas: where in the app the work is done. */
export type CheckArea =
  | 'context'
  | 'members'
  | 'stakeholders'
  | 'sources'
  | 'analysis'
  | 'validation'
  | 'design'
  | 'development'
  | 'testing'
  | 'traceability'
  | 'lifecycle';

// ExitCheckGuidanceService (Application/Lifecycle). Advisory: the backend still enforces every rule.
export interface CheckGuidance {
  key: string;
  kind: CheckKind;
  howTo: string;
  /** For an automatic check: the keys of the checks it follows from. */
  dependsOn: string[];
  blockers: BlockerGroup[];
}

export interface BlockerGroup {
  reason: string;
  kind: CheckKind;
  title: string;
  instruction: string;
  /** Roles whose action counts as lifecycle evidence for this group. */
  fixRoles: string[];
  /** The current user holds one of fixRoles. */
  callerCanFix: boolean;
  callerNote?: string | null;
  /** User ids of project members holding one of fixRoles. */
  assignees: string[];
  destination: { area: CheckArea | string; section?: string | null };
  /** Blocking artifacts the viewer may see (at most 200). */
  items: BlockingItem[];
  totalCount: number;
  /** Blocking artifacts the viewer may not see: counted, never identified. */
  hiddenCount: number;
}

export interface BlockingItem {
  artifactId: string;
  code: string;
  title: string;
  artifactType: number;
  status: number;
  requirementSourceId?: string | null;
  note?: string | null;
}

export interface StageEvidence {
  stage: number;
  label: string;
  ownerRole: string;
  checks: LifecycleCheck[];
  passed: boolean;
}

// StageTransitionKind: 0 Baseline | 1 Advance | 2 Rollback
// StageTransitionState: 0 Pending | 1 Completed | 2 Rejected | 3 Withdrawn
// ReviewDecision: 0 Approved | 1 Rejected | 2 CommentOnly
export const TRANSITION_KIND = { Baseline: 0, Advance: 1, Rollback: 2 } as const;
export const TRANSITION_STATE = { Pending: 0, Completed: 1, Rejected: 2, Withdrawn: 3 } as const;
export const REVIEW_DECISION = { Approved: 0, Rejected: 1, CommentOnly: 2 } as const;

export interface StageReview {
  id: string;
  reviewerUserId: string;
  reviewerRole?: string | null;
  decision: number;
  comment?: string | null;
  reviewedAt: string;
}

export interface StageTransition {
  id: string;
  projectId: string;
  sequence: number;
  kind: number;
  state: number;
  fromStatus?: number | null;
  toStatus: number;
  requestedByUserId: string;
  requestedByRole?: string | null;
  requestedAt: string;
  requestNote?: string | null;
  decidedByUserId?: string | null;
  decidedByRole?: string | null;
  decidedAt?: string | null;
  decisionReason?: string | null;
  isOverride: boolean;
  overrideReason?: string | null;
  evidence: LifecycleCheck[];
  reviews: StageReview[];
}

/** What the caller may do right now, decided by the backend. */
export interface LifecycleActions {
  canAdvance: boolean;
  canSubmitForApproval: boolean;
  canDecide: boolean;
  canWithdraw: boolean;
  canRollback: boolean;
  canOverride: boolean;
  /** The caller would move the stage only as an Administrator standing in for its owner: an override, reason required. */
  advanceIsOverride: boolean;
  /** The caller would approve only as an Administrator standing in for the Reviewer: an override, reason required. */
  decideIsOverride: boolean;
}

export interface ProjectLifecycleState {
  projectId: string;
  status: number;
  stageLabel: string;
  ownerRole: string;
  nextStatus?: number | null;
  nextIsGated: boolean;
  requiresSignOff: boolean;
  exitChecks: LifecycleCheck[];
  readyToAdvance: boolean;
  pendingTransition?: StageTransition | null;
  evidenceGaps: StageEvidence[];
  actions: LifecycleActions;
  history: StageTransition[];
}

export interface LifecycleStagePolicy {
  status: number;
  label: string;
  ownerRole: string;
  next?: number | null;
  leavingIsGated: boolean;
  gateApproverRole?: string | null;
}

export interface AdvanceStageRequest {
  targetStatus: number;
  note?: string;
  overrideReason?: string;
}

export interface DecideStageTransitionRequest {
  approve: boolean;
  reason?: string;
  overrideReason?: string;
}

// WorkOutcome (Domain/Enums/WorkOutcome.cs). Tasks: TaskCompleted | TaskReopened.
// Test cases: TestPassed | TestFailed | TestBlocked. The latest record is the current outcome.
export const WORK_OUTCOME = { TaskCompleted: 0, TaskReopened: 1, TestPassed: 2, TestFailed: 3, TestBlocked: 4 } as const;
export type WorkOutcome = (typeof WORK_OUTCOME)[keyof typeof WORK_OUTCOME];

export interface ArtifactWorkRecord {
  id: string;
  artifactId: string;
  versionNumber: number;
  outcome: WorkOutcome;
  note?: string | null;
  recordedByUserId: string;
  recordedByRole?: string | null;
  recordedAt: string;
}

// ProjectNotificationKind: 0 Handover | 1 ApprovalRequested | 2 TransitionRejected
export const NOTIFICATION_KIND = { Handover: 0, ApprovalRequested: 1, TransitionRejected: 2 } as const;

export interface ProjectNotification {
  id: string;
  projectId: string;
  projectName: string;
  transitionId: string;
  kind: number;
  message: string;
  createdAt: string;
  acknowledgedAt?: string | null;
}

// §26 — RAG document knowledge base.
export interface ProjectDocument {
  id: string;
  projectId: string;
  fileName: string;
  contentType: string;
  uploadedAt: string;
  uploadedByUserId: string;
  chunkCount: number;
}

// §25 — Project Copilot.
export interface CopilotCitation {
  documentName: string;
  chunkIndex: number;
  snippet: string;
}

export interface CopilotAnswer {
  answer: string;
  citations: CopilotCitation[];
  relatedArtifactCodes: string[];
}

// §36 — agent pipeline. Enums arrive as numbers, matching the rest of the API.
// AgentRunStatus: 0 Running | 1 AwaitingApproval | 2 Completed | 3 Rejected | 4 Failed
// AgentStageStatus: 0 Pending | 1 Running | 2 AwaitingApproval | 3 Approved | 4 Rejected | 5 Failed
export interface AgentDefinition {
  agent: number;
  order: number;
  displayName: string;
  responsibility: string;
  inputs: string;
  outputs: string;
  approverRoles: string[];
  /** Lifecycle stage(s) the agent's output serves. Agents never move the project. */
  lifecycleStages: number[];
  lifecycleContribution: string;
}

export interface AgentArtifactRef {
  id: string;
  code: string;
  type: string;
  title: string;
}

export interface AgentOutput {
  summary: string;
  metrics: Record<string, number>;
  producedArtifacts: AgentArtifactRef[];
  findings: string[];
  reviewGuidance: string;
}

/** One agent step of a run (an AI pipeline step - not a project lifecycle stage). */
export interface AgentStep {
  id: string;
  agent: number;
  order: number;
  displayName: string;
  status: number;
  output?: AgentOutput | null;
  error?: string | null;
  approverRoles: string[];
  startedAt?: string | null;
  finishedAt?: string | null;
  decidedByUserId?: string | null;
  decidedAt?: string | null;
  decisionComment?: string | null;
  lifecycleStages: number[];
}

export interface AgentRun {
  id: string;
  projectId: string;
  requirementSourceId: string;
  status: number;
  startedByUserId: string;
  startedAt: string;
  completedAt?: string | null;
  /** The run's agent steps (the API keeps the field name `stages` for compatibility). */
  stages: AgentStep[];
  /** Project lifecycle stage when the run started; null for runs from before it was recorded. */
  projectStatusAtStart?: number | null;
}

export interface AgentRunStepSummary {
  agent: number;
  displayName: string;
  lifecycleStages: number[];
  status: number;
  approverRoles: string[];
  decidedByUserId?: string | null;
  decidedAt?: string | null;
  aheadOfProjectStage: boolean;
}

export interface AgentRunSummaryItem {
  runId: string;
  requirementSourceId: string;
  status: number;
  startedAt: string;
  completedAt?: string | null;
  projectStatusAtStart?: number | null;
  stepsApproved: number;
  stepsRejected: number;
  stepCount: number;
  currentStep?: AgentRunStepSummary | null;
  awaitingHumanDecision: boolean;
  steps: AgentRunStepSummary[];
}

export interface AgentStageActivity {
  stage: number;
  label: string;
  agents: string[];
  stepsAwaitingDecision: number;
  stepsApproved: number;
  stepsRejected: number;
  stepsFailed: number;
}

export interface AgentRunSummary {
  projectId: string;
  currentProjectStatus: number;
  totalRuns: number;
  active: number;
  awaitingDecision: number;
  failed: number;
  completed: number;
  rejected: number;
  byLifecycleStage: AgentStageActivity[];
  recentRuns: AgentRunSummaryItem[];
}

// GET /api/ai/status (AiProviderStatus). Never carries API keys.
export interface AiProviderStatus {
  provider: string;
  configured: boolean;
  modelAvailable: boolean;
  model: string;
  detail: string | null;
  fallback: AiProviderStatus | null;
}

// GET /api/settings (Administrator only, read-only). Values come from server configuration;
// provider credentials are never included.
export interface SystemSettings {
  ai: {
    status: AiProviderStatus;
    fallbackEnabled: boolean;
    primaryCooldownUntil: string | null;
    usingDevelopmentStub: boolean;
    checkedAt: string;
  };
  runtime: {
    environment: string;
    primaryCooldownSeconds: number;
    requestTimeoutSeconds: number;
    maxAttempts: number;
  };
  credentialsNote: string;
}
