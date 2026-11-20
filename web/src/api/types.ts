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
  // Present only on a single-source read, when the uploaded PDF/DOCX itself is kept.
  originalDocument?: OriginalDocumentInfo | null;
}

export interface OriginalDocumentInfo {
  format: 'pdf' | 'docx' | string;
  length: number;
  // PDF only: the rawText line each page starts on.
  pageLineStarts?: number[] | null;
  storedAt: string;
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

// A place in a clarification question's reason that the server resolved in its requirement source
// (SourceReferenceIndex). Line/endLine: 0-based line range of the source text, endLine exclusive.
export interface SourceReference {
  label: string;
  start: number;
  sourceId: string;
  sectionNumber?: string | null;
  heading?: string | null;
  line: number;
  endLine: number;
  anchor: string;
  excerpt?: string | null;
  excerptIsQuote?: boolean;
  // Evidence only: how many places in the source hold the quoted words (null: one).
  occurrences?: number | null;
  // Evidence only: how a question saved without verified evidence was linked to this passage when read.
  recovery?: 'generation-response' | 'quoted-in-question' | null;
}

// How far a clarification question is linked to its source (ClarificationSourceReferences.WithSourceLinks).
export type SourceLinkStatus = 'passage' | 'section' | 'unresolved';

export interface ClarificationQuestionData {
  question: string;
  reason?: string | null;
  clarificationStatus: 'Open' | 'Answered' | 'Resolved' | 'NotApplicable';
  answer?: string | null;
  // Absent for a caller who may not read raw sources (S2).
  sourceReferences?: SourceReference[] | null;
  // Set on questions raised from RequirementAnalysis-v3 onwards (ClarificationCandidateValidator).
  kind?: ClarificationKind | null;
  impact?: string | null;
  // Server-verified quotes from the source (both statements for a contradiction); absent for S2 callers.
  sourceEvidence?: SourceReference[] | null;
  // The requirement IDs or sections the evidence sits in.
  requirementRefs?: string[] | null;
  // Project document passages that may bear on the question (retrieval); absent for S2 callers.
  relatedDocuments?: RelatedDocumentPassage[] | null;
  // Set by the server on every read for callers who may open the source; absent for S2 callers.
  sourceLinkStatus?: SourceLinkStatus | null;
}

export type ClarificationKind = 'missing' | 'ambiguous' | 'contradiction' | 'undefined-rule';

export interface RelatedDocumentPassage {
  reference: string;
  score: number;
  excerpt: string;
}

export interface FunctionalRequirementData extends RequirementEvidenceData {
  actor: string;
  preconditions?: string | null;
  inputs?: string | null;
  processing?: string | null;
  expectedResult?: string | null;
  dependencies: string[];
  // §26 — project document excerpts this requirement is grounded in, e.g. "notes.pdf#3".
  sourceReferences?: string[];
}

// P0-2 — one verbatim quote a generated requirement cites. from/quote/sourceId/ref are the model's;
// status/reason/matchMode/matchCount are the server's check ("verified" = the words were found in
// the cited text, nothing more). For a user who may not read raw sources (S2) the quote text — and
// an excerpt's document label — is withheld (quoteWithheld).
export interface EvidenceQuote {
  from?: string | null;
  quote?: string;
  sourceId?: string | null;
  ref?: string | null;
  status?: 'verified' | 'unverified' | 'none' | null;
  reason?: string | null;
  matchMode?: 'strict' | 'spacing_tolerant' | null;
  matchCount?: number;
  quoteWithheld?: boolean;
}

// P0-2 — origin and evidence fields shared by generated FRs and NFRs. All optional: records made
// before evidence checks have none of them (no evidenceCheck).
export interface RequirementEvidenceData {
  requirementOrigin?: string | null;
  derivationNote?: string | null;
  assumedClarifications?: string[];
  evidence?: EvidenceQuote[];
  evidenceStatus?: 'verified' | 'unverified' | 'none' | null;
  unsupportedNumbers?: string[];
  evidenceCheck?: string | null;
}

export interface NonFunctionalRequirementData extends RequirementEvidenceData {
  category: string;
  description?: string | null;
  targetValue?: string | null;
  assumptionStatus?: 'confirmed' | 'proposed_assumption' | null;
  // Server-set when a BA/Reviewer confirms the target (POST /api/artifacts/{id}/nfr-confirmation).
  confirmedByUserId?: string | null;
  confirmedAt?: string | null;
  // P0-2 — the model's claim about the target ("stated" | "proposed"), and the server's check of a
  // "stated" claim against verified SRS quotes. Neither confirms the target.
  targetOrigin?: string | null;
  targetSupport?: 'quote_found' | 'not_supported' | null;
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
  testKind: FunctionalTestKind | NonFunctionalTestKind;
}

export const FUNCTIONAL_TEST_KINDS = ['positive', 'negative', 'boundary', 'permission', 'validation'] as const;
/** Kinds a non-functional requirement's test cases may also have (AiVocabulary.NfrTestKinds). */
export const NON_FUNCTIONAL_TEST_KINDS = [
  'performance',
  'load',
  'security',
  'availability',
  'reliability',
  'scalability',
  'usability',
  'accessibility',
  'maintainability',
  'compliance',
  'observability',
] as const;
export type FunctionalTestKind = (typeof FUNCTIONAL_TEST_KINDS)[number];
export type NonFunctionalTestKind = (typeof NON_FUNCTIONAL_TEST_KINDS)[number];

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

// GET /api/ai/usage (Administrator) - mirrors AiUsageSnapshot: token usage the provider reported since this
// API instance started. In memory on the server, so it starts at zero after a restart.
export interface AiUsageTokens {
  inputTokens: number;
  outputTokens: number;
  cacheCreationInputTokens: number;
  cacheReadInputTokens: number;
}

export interface AiUsageTotals extends AiUsageTokens {
  requests: number;
}

export interface AiUsageSnapshot {
  since: string;
  totals: AiUsageTotals;
  byModel: { provider: string; model: string; totals: AiUsageTotals }[];
  recent: { at: string; provider: string; model: string; usage: AiUsageTokens }[];
}

// GET /api/projects/{id}/traceability-gaps - mirrors TraceabilityGapsResponse. Orphans are downstream
// artifacts linked to no requirement (or, for acceptance criteria, no story), so no matrix row shows them.
export interface TraceabilityOrphan {
  id: string;
  code: string;
  title: string;
  artifactType: number;
  status: number;
  missingLink: string;
}

export interface TraceabilityGaps {
  orphans: TraceabilityOrphan[];
  untracedNonFunctionalRequirements: TraceRef[];
  nonFunctionalRequirementCount: number;
  /** Each NFR with the test cases that verify it; the matrix has rows for FRs only. Absent from older APIs. */
  nonFunctionalTestCoverage?: NfrTestCoverage[];
}

export interface NfrTestCoverage {
  id: string;
  code: string;
  title: string;
  category: string | null;
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
  // Questions not saved because the same question already exists for this source.
  skippedDuplicates?: number;
  // P0-7 — absent from older API versions.
  completeness?: RequirementCompleteness | null;
  // True when the stored analysis was returned and the AI was not called.
  reused?: boolean;
  stale?: boolean;
  staleReasons?: string[] | null;
  analysisVersion?: string | null;
  analyzedAt?: string | null;
  coverage?: AnalysisCoverageReport | null;
  rejectedCandidates?: RejectedClarification[] | null;
  retrieval?: AnalysisRetrievalReport | null;
  reconciliation?: AnalysisReconciliation | null;
  // Proposed questions not asked because they assume something the source denies, whose gap no saved
  // question covers; kept for a reviewer, never saved as questions. Absent on older analyses.
  heldForReview?: HeldClarification[] | null;
}

// One held item (HeldClarification in ClarificationCandidateValidator.cs). evidence: the passages the
// question cited (line -1 when not placed in the source); denialLine (0-based) / denialText: the source
// passage that contradicts the question's assumption; location: where that is; note: why it was held.
export interface HeldClarification {
  topic: string;
  question: string;
  kind?: string | null;
  evidence: SourceReference[];
  denialLine: number;
  denialText: string;
  location: string;
  note: string;
}

// Whether every line and section of the source was sent to the AI (AnalysisCoverage.cs).
export interface AnalysisCoverageReport {
  status: 'complete' | 'incomplete' | 'unverified';
  method: 'whole' | 'split' | string;
  partCount: number;
  sectionCount: number;
  sectionsCovered: number;
  lineCount: number;
  linesCovered: number;
  missingSections: string[];
  parts: { part: number; sections: string[] }[];
  note?: string | null;
}

export interface CandidateRejection {
  code: string;
  message: string;
  relatedCode?: string | null;
  location?: string | null;
}

// A question the AI proposed that the deterministic validator did not save.
export interface RejectedClarification {
  topic: string;
  question: string;
  kind?: string | null;
  reasons: CandidateRejection[];
}

export interface AnalysisRetrievalReport {
  sourcePassages: string;
  projectDocuments: 'used' | 'no-documents' | 'stale-only' | 'failed' | 'not-run' | string;
  queries: number;
  passagesAttached: number;
  error?: string | null;
}

export interface AnalysisReconciliation {
  existingQuestions: number;
  raisedAgain: string[];
  notRaisedAgain: string[];
}

// P0-7 — how complete an Analyze/Generate result is; response-only (RequirementCompleteness.cs).
// contextReduction: 'none' = sent whole; 'split' = sent in partCount parts, every passage once, so the
// AI could not reason across parts. clarifications: 'whole' = every request carried all of them;
// 'distributed' = each part carried the most relevant ones; 'none' = there were none. The evidence
// counts (Generate only, null for Analyze) cover the requirements this run created, not skipped
// duplicates. Source-ID coverage is the result's sourceIdCoverage, not repeated here.
export interface RequirementCompleteness {
  contextReduction: 'none' | 'split' | string;
  partCount: number;
  clarifications: 'whole' | 'distributed' | 'none' | string;
  models: string[];
  unverifiedEvidenceCount?: number | null;
  noEvidenceCount?: number | null;
  unsupportedNumbersCount?: number | null;
}

export interface GenerateRequirementsResult {
  functionalRequirements: ArtifactSummary[];
  nonFunctionalRequirements: ArtifactSummary[];
  // Requirements not saved because the same requirement already exists for this source.
  skippedDuplicates?: number;
  // P0-4 — coverage after this save (see SourceIdCoverage).
  sourceIdCoverage?: SourceIdCoverage | null;
  // P0-7 — absent from older API versions.
  completeness?: RequirementCompleteness | null;
}

// P0-4 — GET /api/requirement-sources/{id}/source-id-coverage (refused to a user who may not read raw
// sources). Requirement IDs are "defined" when they start a line (after a bullet, number or "|") and
// "mentioned only" otherwise; a defined ID is covered when a saved, non-rejected FR/NFR cites it in
// sourceIds. uncovered includes the IDs in coveredOnlyByRejected. not_applicable: no defined IDs, so
// coverage cannot be checked (never "complete"). IDs are in source order, as written in the source.
export type SourceIdCoverageStatus = 'not_applicable' | 'complete' | 'incomplete';

export interface SourceIdCoverage {
  status: SourceIdCoverageStatus;
  definedCount: number;
  covered: string[];
  uncovered: string[];
  coveredOnlyByRejected: string[];
  mentionedOnly: string[];
}

export interface ClarificationAnswerRequest {
  answer: string;
  notApplicable: boolean;
}

export interface UpdateArtifactStatusRequest {
  status: number;
  comment?: string;
  // S1 — for an approve/reject: the version decided on; the API refuses (409) if it is no longer current.
  version?: number;
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
  /**
   * D3 - true when `evidence` is known not to be the submission-time evidence (an approved gated
   * request recorded without a decision snapshot). Availability only: false or absent (older API
   * responses) certifies nothing and says nothing about when the row was written.
   */
  submissionEvidenceUnavailable?: boolean;
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

// What project context the answer was given (CopilotContextReport on the server). Every count
// describes the prompt actually sent, and only content the user may see. The four artifact counts
// are mutually exclusive. excerptsOmitted is null when the user may not read raw sources.
export interface CopilotContextReport {
  reduced: boolean;
  reason: 'standardLimits' | 'providerBudget' | null;
  provider: string | null;
  inputBudget: number | null;
  artifactsFull: number;
  artifactsTruncated: number;
  artifactsTitleOnly: number;
  artifactsOmitted: number;
  versionsOmitted: number;
  impactCodesOmitted: number;
  excerptsOmitted: number | null;
}

export interface CopilotAnswer {
  answer: string;
  citations: CopilotCitation[];
  relatedArtifactCodes: string[];
  // Absent from older servers.
  context?: CopilotContextReport | null;
}

export interface CopilotLimits {
  maxQuestionChars: number;
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

// ---- Lifecycle review (GET /api/projects/{id}/lifecycle-review) ---------------------------------
// A read-only, historical projection: what each lifecycle stage produced, by whom and from what.
// A null stage means the backend could not determine it from recorded history.

export interface ArtifactTypeCount {
  artifactType: number;
  count: number;
  aiGenerated: number;
  manual: number;
}

export interface StageVisit {
  enteredAt: string;
  leftAt?: string | null;
  enteredBy: number;
  enteredByUserId?: string | null;
  enteredByRole?: string | null;
  leftBy?: number | null;
}

/** StageReviewState on the server. */
export const STAGE_REVIEW_STATE = { Completed: 0, Current: 1, NotReached: 2 } as const;

export interface StageActivity {
  artifactsCreated: number;
  aiGenerated: number;
  manual: number;
  artifactsEdited: number;
  versionsRecorded: number;
  approvals: number;
  rejections: number;
  comments: number;
  aiOperations: number;
  agentStepsRun: number;
  tasksCompleted: number;
  testsPassed: number;
  testsFailed: number;
  testsBlocked: number;
  requirementSourcesAdded: number;
  touchedArtifactIds: string[];
}

export interface StageReviewSummary {
  stage: number;
  label: string;
  ownerRole: string;
  state: number;
  visits: StageVisit[];
  firstEnteredAt?: string | null;
  completedAt?: string | null;
  historyUnavailable: boolean;
  createdArtifactIds: string[];
  createdByType: ArtifactTypeCount[];
  activity: StageActivity;
  transitions: StageTransition[];
}

export interface ArtifactAiProvenance {
  executionId: string;
  operationType: string;
  model: string;
  promptTemplateVersion?: string | null;
  userId: string;
  timestamp: string;
  accepted?: boolean | null;
  agentRunId?: string | null;
  agent?: number | null;
}

export interface ReviewedArtifact {
  id: string;
  code: string;
  title: string;
  artifactType: number;
  status: number;
  origin: number;
  currentVersion: number;
  approvedVersion?: number | null;
  versionCount: number;
  createdByUserId: string;
  createdAt: string;
  updatedByUserId?: string | null;
  updatedAt: string;
  createdInStage?: number | null;
  servesStage: number;
  requirementSourceId?: string | null;
  ai?: ArtifactAiProvenance | null;
  latestReview?: { reviewerUserId: string; reviewerRole?: string | null; decision: number; versionNumber?: number | null; comment?: string | null; reviewedAt: string } | null;
  reviewCount: number;
  latestWork?: { outcome: number; versionNumber: number; recordedByUserId: string; recordedByRole?: string | null; recordedAt: string; note?: string | null } | null;
  links: { relatedArtifactId: string; code: string; artifactType: number; relationshipType: number; isOutgoing: boolean }[];
}

export interface ReviewedRequirementSource {
  id: string;
  sourceType: number;
  fileName?: string | null;
  createdByUserId: string;
  createdAt: string;
  createdInStage?: number | null;
}

/** TimelineEventKind on the server. */
export const TIMELINE_EVENT_KIND = {
  ProjectCreated: 0,
  StageTransition: 1,
  RequirementSourceAdded: 2,
  AiGeneration: 3,
  AgentStep: 4,
  ArtifactsCreated: 5,
  ArtifactsEdited: 6,
  ArtifactsReviewed: 7,
  WorkRecorded: 8,
} as const;

export interface ReviewTimelineEvent {
  at: string;
  kind: number;
  title: string;
  detail?: string | null;
  actorUserId?: string | null;
  actorRole?: string | null;
  stage?: number | null;
  count: number;
  artifactIds: string[];
}

export interface LifecycleReview {
  projectId: string;
  projectName: string;
  currentStatus: number;
  isCompleted: boolean;
  completedAt?: string | null;
  historyComplete: boolean;
  definition: {
    description?: string | null;
    businessProblem?: string | null;
    objectives?: string | null;
    scope?: string | null;
    createdByUserId: string;
    createdAt: string;
    updatedAt: string;
  };
  totals: {
    totalArtifacts: number;
    aiGenerated: number;
    manual: number;
    unattributedArtifacts: number;
    byType: ArtifactTypeCount[];
    versions: number;
    reviews: number;
    aiOperations: number;
    agentRuns: number;
    tasksCompleted: number;
    testsPassed: number;
    testsFailed: number;
    testsBlocked: number;
    functionalRequirements: number;
    fullyTracedFunctionalRequirements: number;
    traceabilityCoveragePercent?: number | null;
  };
  stages: StageReviewSummary[];
  artifacts: ReviewedArtifact[];
  requirementSources: ReviewedRequirementSource[];
  timeline: ReviewTimelineEvent[];
  traceability: TraceabilityRow[];
}

// Dry-run AI plan (GET /api/requirement-sources/{id}/ai-plan) - mirrors AiPlanResult. Nothing is
// sent to any AI provider to make it; token figures are estimates and provider quota is unknown.
export type AiPlanOperation = 'analyze' | 'generate';
export type AiPlanStatusValue = 'WithinLimits' | 'Warning' | 'Uncertain' | 'Blocked';
export type AiPlanAvailabilityValue = 'QuotaUnknown' | 'CoolingDown' | 'Stub' | 'NotConfigured';

export interface AiPlanRange {
  min: number;
  max: number;
  exact: boolean;
}

export interface AiPlanReason {
  code: string;
  severity: 'Blocking' | 'Warning' | 'Info';
  message: string;
  setting?: string | null;
  limit?: number | null;
  actual?: number | null;
  until?: string | null;
}

export interface AiPlanProvider {
  role: 'Primary' | 'Fallback';
  provider: string;
  model: string;
  status: AiPlanStatusValue;
  sizeStatus: AiPlanStatusValue;
  availability: AiPlanAvailabilityValue;
  cooldownUntil: string | null;
  wholeRequest: boolean;
  parts: AiPlanRange | null;
  estimatedRequestTokens: number;
  inputBudget: number | null;
  maxRequestTokens: number | null;
  maxPartSourceTokens: number | null;
  reservedTokensPerPart: number | null;
  clarificationMode: 'Whole' | 'Subset' | null;
  maxParts: number;
  maxPartsSetting: string | null;
  estimatedTotalTokensUpperBound: number | null;
  maxOutputTokens: number | null;
  timeoutSeconds: number | null;
  reasons: AiPlanReason[];
}

export interface AiPlanResult {
  operation: AiPlanOperation;
  requirementSourceId: string;
  generatedAt: string;
  context: {
    sourceTokens: number;
    systemPromptTokens: number;
    fixedContextTokens: AiPlanRange;
    clarificationItems: number;
    clarificationTokens: number;
    businessObjectives: number;
    documentExcerptCandidates: number;
  };
  route: {
    primary: string | null;
    fallback: string | null;
    primaryCooldownUntil: string | null;
    expectedFirst: string | null;
    configurationError: string | null;
  };
  providers: AiPlanProvider[];
  overall: { status: AiPlanStatusValue; summary: string };
  caveats: string[];
  batch?: AiPlanBatch | null;
}

// Batched (resumable) AI runs - mirrors AiChunkRunResponse. Only when Ai:Groq:MaxBatchedRunParts > 0.
export type AiChunkRunStatusValue = 'Running' | 'Staged' | 'Committed' | 'Failed' | 'Superseded' | 'Abandoned';

export interface AiChunkRunPartSummary {
  number: number;
  status: 'Pending' | 'Succeeded' | 'Failed';
  attempts: number;
  section: string | null;
  model: string | null;
  lastError: string | null;
}

export interface AiChunkRun {
  id: string;
  requirementSourceId: string;
  operation: AiPlanOperation;
  status: AiChunkRunStatusValue;
  partCount: number;
  succeededParts: number;
  failedParts: number;
  pendingParts: number;
  partsPerAction: number;
  remainingActions: number;
  budgetProvider: string | null;
  lastError: string | null;
  lastErrorRetryable: boolean;
  leaseActive: boolean;
  startedAt: string;
  updatedAt: string;
  completedAt: string | null;
  // sourceIdCoverage: only on the response to a generation run's Commit (not stored with the run).
  result: { createdArtifactIds: string[]; skippedDuplicates: number; sourceIdCoverage?: SourceIdCoverage | null } | null;
  parts: AiChunkRunPartSummary[];
}

// AiPlanResult.batch - enabled false when batch mode is off (nothing else filled).
export interface AiPlanBatch {
  enabled: boolean;
  maxRunParts: number;
  budgetProvider: string | null;
  partsPerAction: number | null;
  parts: number | null;
  actions: number | null;
  canStart: boolean;
  reason: string | null;
  activeRun: AiChunkRun | null;
}
