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

export interface RegisterRequest {
  email: string;
  password: string;
  displayName: string;
  role: string;
}

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
}

export interface ArtifactVersion {
  versionNumber: number;
  data: Record<string, unknown>;
  changedByUserId: string;
  changedAt: string;
  reason?: string | null;
  origin: number;
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
}

export interface NonFunctionalRequirementData {
  category: string;
  description?: string | null;
  targetValue?: string | null;
  assumptionStatus?: 'confirmed' | 'proposed_assumption' | null;
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
  recentChanges: RecentChangeItem[];
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
