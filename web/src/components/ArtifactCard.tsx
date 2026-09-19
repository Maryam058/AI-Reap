import { useState } from 'react';
import { ArtifactDetails } from './ArtifactDetails';
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
  onApprove: (id: string) => Promise<void>;
  onReject: (id: string) => Promise<void>;
  onGenerateAcceptanceCriteria?: (userStoryId: string) => Promise<void>;
  onGenerateTestCases?: (functionalRequirementId: string) => Promise<void>;
}

function Detail({ label, value }: { label: string; value?: string | null }) {
  if (!value) return null;
  return (
    <p>
      <span className="detail-label">{label}:</span> {value}
    </p>
  );
}

export function ArtifactCard({ artifact, canReview, onApprove, onReject, onGenerateAcceptanceCriteria, onGenerateTestCases }: Props) {
  const [busy, setBusy] = useState(false);

  const isPending = artifact.status === 0 || artifact.status === 1 || artifact.status === 2;
  const isAiOrigin = artifact.origin === 0;

  const act = async (fn: (id: string) => Promise<void>) => {
    setBusy(true);
    try {
      await fn(artifact.id);
    } finally {
      setBusy(false);
    }
  };

  return (
    <li className="artifact-card">
      <div className="artifact-header">
        <span className="code">{artifact.code}</span>
        <strong>{artifact.title}</strong>
        {artifact.priority !== null && <span className="priority-pill">{ARTIFACT_PRIORITY_LABELS[artifact.priority]}</span>}
        <span className="status-pill">{ARTIFACT_STATUS_LABELS[artifact.status]}</span>
        {isAiOrigin && <span className="ai-badge">AI Generated</span>}
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
        {canReview && isPending && (
          <>
            <button type="button" disabled={busy} onClick={() => act(onApprove)}>
              Approve
            </button>
            <button type="button" className="secondary" disabled={busy} onClick={() => act(onReject)}>
              Reject
            </button>
          </>
        )}
        {artifact.artifactType === ARTIFACT_TYPE_VALUES.UserStory && onGenerateAcceptanceCriteria && (
          <button type="button" disabled={busy} onClick={() => act(onGenerateAcceptanceCriteria)}>
            Generate acceptance criteria
          </button>
        )}
        {artifact.artifactType === ARTIFACT_TYPE_VALUES.FunctionalRequirement && onGenerateTestCases && (
          <button type="button" disabled={busy} onClick={() => act(onGenerateTestCases)}>
            Generate test cases
          </button>
        )}
      </div>

      <ArtifactDetails artifactId={artifact.id} artifactType={artifact.artifactType} />
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
        <p>
          <span className="detail-label">Steps:</span>
          <ol className="test-steps">
            {data.steps.map((s, i) => (
              <li key={i}>{s}</li>
            ))}
          </ol>
        </p>
      )}
      <Detail label="Expected result" value={data.expectedResult} />
    </>
  );
}
