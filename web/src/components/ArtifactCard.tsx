import { useState } from 'react';
import { ArtifactDetails } from './ArtifactDetails';
import { ArtifactEditDialog } from './ArtifactEditDialog';
import { IconCheck, IconEdit, IconSparkles, IconX } from './icons';
import {
  ARTIFACT_PRIORITY_LABELS,
  ARTIFACT_STATUS_LABELS,
  ARTIFACT_TYPE_VALUES,
  type AcceptanceCriterionData,
  type ApiSpecificationData,
  type ArtifactSummary,
  type BusinessRuleData,
  type DataEntityData,
  type DesignArtifactData,
  type FunctionalRequirementData,
  type ImplementationTaskData,
  type NonFunctionalRequirementData,
  type TestCaseData,
  type UserStoryData,
} from '../api/types';

interface Props {
  artifact: ArtifactSummary;
  canReview: boolean;
  // §23 — Administrator/BusinessAnalyst only (the backend's WriterRoles on PATCH
  // /api/artifacts/{id}), distinct from canReview (which also includes the Reviewer role).
  canEdit: boolean;
  token: string | null;
  onApprove: (id: string) => Promise<void>;
  onReject: (id: string) => Promise<void>;
  onSaved: () => void;
  onGenerateAcceptanceCriteria?: (userStoryId: string) => Promise<void>;
  onGenerateTestCases?: (functionalRequirementId: string) => Promise<void>;
}

// Artifact status (0 AI Generated … 6 Verified) → status-pill tone.
const STATUS_TONE: Record<number, string> = {
  0: 'info',
  1: 'neutral',
  2: 'warning',
  3: 'success',
  4: 'danger',
  5: 'info',
  6: 'success',
};

// Artifact priority (0 Low … 3 Critical) → status-pill tone.
const PRIORITY_TONE: Record<number, string> = {
  0: 'neutral',
  1: 'info',
  2: 'warning',
  3: 'danger',
};

function Detail({ label, value }: { label: string; value?: string | null }) {
  if (!value) return null;
  return (
    <p className="detail-row">
      <span className="detail-label">{label}</span>
      <span className="detail-value">{value}</span>
    </p>
  );
}

export function ArtifactCard({
  artifact,
  canReview,
  canEdit,
  token,
  onApprove,
  onReject,
  onSaved,
  onGenerateAcceptanceCriteria,
  onGenerateTestCases,
}: Props) {
  const [busy, setBusy] = useState(false);
  const [editing, setEditing] = useState(false);

  const isPending = artifact.status === 0 || artifact.status === 1 || artifact.status === 2;
  const isAiOrigin = artifact.origin === 0;
  const isEditable = canEdit && artifact.artifactType !== ARTIFACT_TYPE_VALUES.ClarificationQuestion;

  const act = async (fn: (id: string) => Promise<void>) => {
    setBusy(true);
    try {
      await fn(artifact.id);
    } finally {
      setBusy(false);
    }
  };

  return (
    <li className={`artifact-card tone-${STATUS_TONE[artifact.status] ?? 'neutral'}`}>
      <div className="artifact-header">
        <span className="code">{artifact.code}</span>
        <strong className="artifact-title">{artifact.title}</strong>
        <span className="artifact-badges">
          {artifact.priority !== null && (
            <span className={`status-pill tone-${PRIORITY_TONE[artifact.priority] ?? 'neutral'}`}>
              {ARTIFACT_PRIORITY_LABELS[artifact.priority]}
            </span>
          )}
          <span className={`status-pill tone-${STATUS_TONE[artifact.status] ?? 'neutral'}`}>
            {ARTIFACT_STATUS_LABELS[artifact.status]}
          </span>
          {isAiOrigin && <span className="ai-badge">AI Generated</span>}
        </span>
      </div>

      <div className="artifact-body">
        {artifact.artifactType === ARTIFACT_TYPE_VALUES.FunctionalRequirement && (
          <FunctionalRequirementDetails data={artifact.data as unknown as FunctionalRequirementData} />
        )}
        {artifact.artifactType === ARTIFACT_TYPE_VALUES.NonFunctionalRequirement && (
          <NonFunctionalRequirementDetails data={artifact.data as unknown as NonFunctionalRequirementData} />
        )}
        {artifact.artifactType === ARTIFACT_TYPE_VALUES.UserStory && (
          <UserStoryDetails data={artifact.data as unknown as UserStoryData} />
        )}
        {artifact.artifactType === ARTIFACT_TYPE_VALUES.AcceptanceCriterion && (
          <AcceptanceCriterionDetails data={artifact.data as unknown as AcceptanceCriterionData} />
        )}
        {artifact.artifactType === ARTIFACT_TYPE_VALUES.BusinessRule && (
          <Detail label="Statement" value={(artifact.data as unknown as BusinessRuleData).statement} />
        )}
        {artifact.artifactType === ARTIFACT_TYPE_VALUES.DesignArtifact && (
          <DesignArtifactDetails data={artifact.data as unknown as DesignArtifactData} />
        )}
        {artifact.artifactType === ARTIFACT_TYPE_VALUES.DataEntity && (
          <DataEntityDetails data={artifact.data as unknown as DataEntityData} />
        )}
        {artifact.artifactType === ARTIFACT_TYPE_VALUES.ApiSpecification && (
          <ApiSpecificationDetails data={artifact.data as unknown as ApiSpecificationData} />
        )}
        {artifact.artifactType === ARTIFACT_TYPE_VALUES.ImplementationTask && (
          <ImplementationTaskDetails data={artifact.data as unknown as ImplementationTaskData} />
        )}
        {artifact.artifactType === ARTIFACT_TYPE_VALUES.TestCase && (
          <TestCaseDetails data={artifact.data as unknown as TestCaseData} />
        )}
      </div>

      <div className="artifact-actions">
        {isEditable && (
          <button type="button" className="btn btn-secondary btn-sm" disabled={busy} onClick={() => setEditing(true)}>
            <IconEdit width={14} height={14} />
            Edit
          </button>
        )}
        {canReview && isPending && (
          <>
            <button type="button" className="btn btn-primary btn-sm" disabled={busy} onClick={() => act(onApprove)}>
              <IconCheck width={14} height={14} />
              Approve
            </button>
            <button type="button" className="btn btn-secondary btn-sm btn-reject" disabled={busy} onClick={() => act(onReject)}>
              <IconX width={14} height={14} />
              Reject
            </button>
          </>
        )}
        {artifact.artifactType === ARTIFACT_TYPE_VALUES.UserStory && onGenerateAcceptanceCriteria && (
          <button type="button" className="btn btn-ghost btn-sm" disabled={busy} onClick={() => act(onGenerateAcceptanceCriteria)}>
            <IconSparkles width={14} height={14} />
            Generate acceptance criteria
          </button>
        )}
        {artifact.artifactType === ARTIFACT_TYPE_VALUES.FunctionalRequirement && onGenerateTestCases && (
          <button type="button" className="btn btn-ghost btn-sm" disabled={busy} onClick={() => act(onGenerateTestCases)}>
            <IconSparkles width={14} height={14} />
            Generate test cases
          </button>
        )}
      </div>

      <ArtifactDetails artifactId={artifact.id} artifactType={artifact.artifactType} />

      {editing && <ArtifactEditDialog artifact={artifact} token={token} onClose={() => setEditing(false)} onSaved={onSaved} />}
    </li>
  );
}

function FunctionalRequirementDetails({ data }: { data: FunctionalRequirementData }) {
  return (
    <>
      <Detail label="Actor" value={data.actor} />
      <Detail label="Preconditions" value={data.preconditions} />
      <Detail label="Inputs" value={data.inputs} />
      <Detail label="Processing" value={data.processing} />
      <Detail label="Expected result" value={data.expectedResult} />
      {data.dependencies?.length > 0 && <Detail label="Dependencies" value={data.dependencies.join(', ')} />}
    </>
  );
}

function NonFunctionalRequirementDetails({ data }: { data: NonFunctionalRequirementData }) {
  return (
    <>
      <Detail label="Category" value={data.category} />
      <Detail label="Description" value={data.description} />
      <Detail label="Target" value={data.targetValue} />
      {data.assumptionStatus && (
        <p>
          <span className={`assumption-pill assumption-${data.assumptionStatus}`}>
            {data.assumptionStatus === 'proposed_assumption' ? 'Proposed assumption — needs confirmation' : 'Confirmed'}
          </span>
        </p>
      )}
    </>
  );
}

function UserStoryDetails({ data }: { data: UserStoryData }) {
  return (
    <>
      <Detail label="Persona" value={data.persona} />
      <Detail label="Value" value={data.valueStatement} />
    </>
  );
}

function AcceptanceCriterionDetails({ data }: { data: AcceptanceCriterionData }) {
  return (
    <p className="gwt">
      <span className={`kind-pill kind-${data.kind}`}>{data.kind}</span>
      <br />
      <strong>Given</strong> {data.given} <strong>When</strong> {data.when} <strong>Then</strong> {data.then}
    </p>
  );
}

function DesignArtifactDetails({ data }: { data: DesignArtifactData }) {
  return (
    <>
      <Detail label="Architecture" value={data.architectureOverview} />
      {data.modules?.length > 0 && <Detail label="Modules" value={data.modules.join(', ')} />}
      <Detail label="Integrations" value={data.integrationPoints} />
      <Detail label="Authentication" value={data.authentication} />
      <Detail label="Background processing" value={data.backgroundProcessing} />
      <Detail label="Caching" value={data.caching} />
      <Detail label="Logging" value={data.logging} />
      <Detail label="Deployment" value={data.deploymentConsiderations} />
    </>
  );
}

function DataEntityDetails({ data }: { data: DataEntityData }) {
  return (
    <>
      {data.fields?.length > 0 && (
        <Detail label="Fields" value={data.fields.map((f) => `${f.name}: ${f.dataType}${f.required ? ' (required)' : ''}`).join(', ')} />
      )}
      {data.keys?.length > 0 && <Detail label="Keys" value={data.keys.join(', ')} />}
      <Detail label="Relationships" value={data.relationships} />
      {data.indexes?.length > 0 && <Detail label="Indexes" value={data.indexes.join(', ')} />}
    </>
  );
}

function ApiSpecificationDetails({ data }: { data: ApiSpecificationData }) {
  return (
    <>
      <p>
        <span className="code">
          {data.method} {data.route}
        </span>
      </p>
      <Detail label="Purpose" value={data.purpose} />
      <Detail label="Request" value={data.request} />
      <Detail label="Response" value={data.response} />
      <Detail label="Validation" value={data.validation} />
      <Detail label="Authorization" value={data.authorization} />
    </>
  );
}

function ImplementationTaskDetails({ data }: { data: ImplementationTaskData }) {
  return (
    <>
      <Detail label="Type" value={data.taskType} />
      <Detail label="Description" value={data.description} />
      <Detail label="Suggested role" value={data.suggestedRole} />
      <Detail label="Estimate" value={data.estimate} />
      {data.dependencies?.length > 0 && <Detail label="Dependencies" value={data.dependencies.join(', ')} />}
    </>
  );
}

function TestCaseDetails({ data }: { data: TestCaseData }) {
  return (
    <>
      <span className={`kind-pill kind-${data.testKind}`}>{data.testKind}</span>
      <Detail label="Preconditions" value={data.preconditions} />
      {data.steps?.length > 0 && (
        <div className="detail-row">
          <span className="detail-label">Steps</span>
          <ol className="test-steps detail-value">
            {data.steps.map((s, i) => (
              <li key={i}>{s}</li>
            ))}
          </ol>
        </div>
      )}
      <Detail label="Expected result" value={data.expectedResult} />
    </>
  );
}
