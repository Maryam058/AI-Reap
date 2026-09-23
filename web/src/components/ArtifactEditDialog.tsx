import { useState } from 'react';
import { Dialog } from './ui/Dialog';
import { TextField } from './ui/TextField';
import { TextArea } from './ui/TextArea';
import { IconX } from './icons';
import { artifactsApi } from '../api/artifacts';
import { ApiError } from '../api/client';
import {
  ARTIFACT_TYPE_VALUES,
  type AcceptanceCriterionData,
  type ApiSpecificationData,
  type ArtifactSummary,
  type BusinessRuleData,
  type DataEntityData,
  type DataEntityField,
  type DesignArtifactData,
  type FunctionalRequirementData,
  type ImplementationTaskData,
  type NonFunctionalRequirementData,
  type TestCaseData,
  type UserStoryData,
} from '../api/types';

interface Props {
  artifact: ArtifactSummary;
  token: string | null;
  onClose: () => void;
  onSaved: () => void;
}

const linesToList = (text: string) =>
  text
    .split('\n')
    .map((s) => s.trim())
    .filter(Boolean);
const listToLines = (items: string[] | undefined | null) => (items ?? []).join('\n');

// §23 human edit form. ClarificationQuestion is not editable here - it has its own
// answer/resolve workflow (ClarificationQuestionCard), not a content edit.
export function ArtifactEditDialog({ artifact, token, onClose, onSaved }: Props) {
  const [title, setTitle] = useState(artifact.title);
  const [reason, setReason] = useState('');
  const [data, setData] = useState<Record<string, unknown>>({ ...artifact.data });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const set = (key: string, value: unknown) => setData((d) => ({ ...d, [key]: value }));

  const onSubmit = async () => {
    setSaving(true);
    setError(null);
    try {
      await artifactsApi.update(artifact.id, { title, data, reason: reason.trim() || undefined }, token);
      onSaved();
      onClose();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not save changes.');
    } finally {
      setSaving(false);
    }
  };

  return (
    <Dialog
      title={`Edit ${artifact.code}`}
      description="Saving creates a new version; the previous content stays in history."
      onClose={onClose}
      width={620}
      footer={
        <>
          <button type="button" className="btn btn-secondary" onClick={onClose} disabled={saving}>
            Cancel
          </button>
          <button type="button" className="btn btn-primary" onClick={onSubmit} disabled={saving || !title.trim()} data-autofocus>
            {saving ? 'Saving…' : 'Save changes'}
          </button>
        </>
      }
    >
      {error && <p className="error">{error}</p>}
      <TextField label="Title" value={title} onChange={setTitle} />
      <TypeFields artifactType={artifact.artifactType} data={data} set={set} />
      <TextField label="Reason for this change (optional)" value={reason} onChange={setReason} placeholder="Why is this being edited?" />
    </Dialog>
  );
}

function TypeFields({
  artifactType,
  data,
  set,
}: {
  artifactType: number;
  data: Record<string, unknown>;
  set: (key: string, value: unknown) => void;
}) {
  switch (artifactType) {
    case ARTIFACT_TYPE_VALUES.FunctionalRequirement: {
      const d = data as unknown as FunctionalRequirementData;
      return (
        <>
          <TextField label="Actor" value={d.actor ?? ''} onChange={(v) => set('actor', v)} />
          <TextArea label="Preconditions" value={d.preconditions ?? ''} onChange={(v) => set('preconditions', v)} />
          <TextArea label="Inputs" value={d.inputs ?? ''} onChange={(v) => set('inputs', v)} />
          <TextArea label="Processing" value={d.processing ?? ''} onChange={(v) => set('processing', v)} />
          <TextArea label="Expected result" value={d.expectedResult ?? ''} onChange={(v) => set('expectedResult', v)} />
          <TextArea label="Dependencies" hint="One per line." value={listToLines(d.dependencies)} onChange={(v) => set('dependencies', linesToList(v))} />
        </>
      );
    }
    case ARTIFACT_TYPE_VALUES.NonFunctionalRequirement: {
      const d = data as unknown as NonFunctionalRequirementData;
      return (
        <>
          <TextField label="Category" value={d.category ?? ''} onChange={(v) => set('category', v)} />
          <TextArea label="Description" value={d.description ?? ''} onChange={(v) => set('description', v)} />
          <TextField
            label="Target value"
            value={d.targetValue ?? ''}
            onChange={(v) => set('targetValue', v)}
            hint="A target value here always stays marked as a proposed assumption (§10, server-enforced) until confirmed elsewhere - editing this text does not change that flag."
          />
        </>
      );
    }
    case ARTIFACT_TYPE_VALUES.BusinessRule: {
      const d = data as unknown as BusinessRuleData;
      return <TextArea label="Statement" value={d.statement ?? ''} onChange={(v) => set('statement', v)} rows={4} />;
    }
    case ARTIFACT_TYPE_VALUES.UserStory: {
      const d = data as unknown as UserStoryData;
      return (
        <>
          <TextField label="Persona" value={d.persona ?? ''} onChange={(v) => set('persona', v)} />
          <TextArea label="Value statement" value={d.valueStatement ?? ''} onChange={(v) => set('valueStatement', v)} />
        </>
      );
    }
    case ARTIFACT_TYPE_VALUES.AcceptanceCriterion: {
      const d = data as unknown as AcceptanceCriterionData;
      return (
        <>
          <TextArea label="Given" value={d.given ?? ''} onChange={(v) => set('given', v)} rows={2} />
          <TextArea label="When" value={d.when ?? ''} onChange={(v) => set('when', v)} rows={2} />
          <TextArea label="Then" value={d.then ?? ''} onChange={(v) => set('then', v)} rows={2} />
          <label className="field">
            <span className="field-label">Kind</span>
            <select className="select-input" value={d.kind ?? 'positive'} onChange={(e) => set('kind', e.target.value)}>
              <option value="positive">Positive</option>
              <option value="negative">Negative</option>
              <option value="boundary">Boundary</option>
            </select>
          </label>
        </>
      );
    }
    case ARTIFACT_TYPE_VALUES.DesignArtifact: {
      const d = data as unknown as DesignArtifactData;
      return (
        <>
          <TextArea label="Architecture overview" value={d.architectureOverview ?? ''} onChange={(v) => set('architectureOverview', v)} rows={4} />
          <TextArea label="Modules" hint="One per line." value={listToLines(d.modules)} onChange={(v) => set('modules', linesToList(v))} />
          <TextArea label="Integration points" value={d.integrationPoints ?? ''} onChange={(v) => set('integrationPoints', v)} />
          <TextField label="Authentication" value={d.authentication ?? ''} onChange={(v) => set('authentication', v)} />
          <TextArea label="Background processing" value={d.backgroundProcessing ?? ''} onChange={(v) => set('backgroundProcessing', v)} />
          <TextArea label="Caching" value={d.caching ?? ''} onChange={(v) => set('caching', v)} />
          <TextArea label="Logging" value={d.logging ?? ''} onChange={(v) => set('logging', v)} />
          <TextArea label="Deployment considerations" value={d.deploymentConsiderations ?? ''} onChange={(v) => set('deploymentConsiderations', v)} />
        </>
      );
    }
    case ARTIFACT_TYPE_VALUES.DataEntity: {
      const d = data as unknown as DataEntityData;
      const fields = d.fields ?? [];
      const setFields = (next: DataEntityField[]) => set('fields', next);
      return (
        <>
          <div className="field">
            <span className="field-label">Fields</span>
            {fields.map((f, i) => (
              <div key={i} className="edit-field-row">
                <input
                  value={f.name}
                  placeholder="Name"
                  onChange={(e) => setFields(fields.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))}
                />
                <input
                  value={f.dataType}
                  placeholder="Type"
                  onChange={(e) => setFields(fields.map((x, j) => (j === i ? { ...x, dataType: e.target.value } : x)))}
                />
                <label className="edit-field-required">
                  <input
                    type="checkbox"
                    checked={f.required}
                    onChange={(e) => setFields(fields.map((x, j) => (j === i ? { ...x, required: e.target.checked } : x)))}
                  />
                  Required
                </label>
                <button type="button" className="icon-btn" onClick={() => setFields(fields.filter((_, j) => j !== i))} aria-label="Remove field">
                  <IconX width={14} height={14} />
                </button>
              </div>
            ))}
            <button type="button" className="btn btn-secondary btn-sm" onClick={() => setFields([...fields, { name: '', dataType: '', required: false }])}>
              Add field
            </button>
          </div>
          <TextArea label="Keys" hint="One per line." value={listToLines(d.keys)} onChange={(v) => set('keys', linesToList(v))} />
          <TextArea label="Relationships" value={d.relationships ?? ''} onChange={(v) => set('relationships', v)} />
          <TextArea label="Indexes" hint="One per line." value={listToLines(d.indexes)} onChange={(v) => set('indexes', linesToList(v))} />
        </>
      );
    }
    case ARTIFACT_TYPE_VALUES.ApiSpecification: {
      const d = data as unknown as ApiSpecificationData;
      return (
        <>
          <TextField label="Purpose" value={d.purpose ?? ''} onChange={(v) => set('purpose', v)} />
          <TextField label="Method" value={d.method ?? ''} onChange={(v) => set('method', v)} />
          <TextField label="Route" value={d.route ?? ''} onChange={(v) => set('route', v)} />
          <TextArea label="Request" value={d.request ?? ''} onChange={(v) => set('request', v)} />
          <TextArea label="Response" value={d.response ?? ''} onChange={(v) => set('response', v)} />
          <TextArea label="Validation" value={d.validation ?? ''} onChange={(v) => set('validation', v)} />
          <TextField label="Authorization" value={d.authorization ?? ''} onChange={(v) => set('authorization', v)} />
        </>
      );
    }
    case ARTIFACT_TYPE_VALUES.ImplementationTask: {
      const d = data as unknown as ImplementationTaskData;
      return (
        <>
          <TextArea label="Description" value={d.description ?? ''} onChange={(v) => set('description', v)} />
          <TextField label="Task type" value={d.taskType ?? ''} onChange={(v) => set('taskType', v)} />
          <TextArea label="Dependencies" hint="One per line." value={listToLines(d.dependencies)} onChange={(v) => set('dependencies', linesToList(v))} />
          <TextField label="Suggested role" value={d.suggestedRole ?? ''} onChange={(v) => set('suggestedRole', v)} />
          <TextField label="Estimate" value={d.estimate ?? ''} onChange={(v) => set('estimate', v)} />
        </>
      );
    }
    case ARTIFACT_TYPE_VALUES.TestCase: {
      const d = data as unknown as TestCaseData;
      return (
        <>
          <TextArea label="Preconditions" value={d.preconditions ?? ''} onChange={(v) => set('preconditions', v)} />
          <TextArea label="Steps" hint="One step per line, in order." value={listToLines(d.steps)} onChange={(v) => set('steps', linesToList(v))} rows={4} />
          <TextArea label="Expected result" value={d.expectedResult ?? ''} onChange={(v) => set('expectedResult', v)} />
          <label className="field">
            <span className="field-label">Kind</span>
            <select className="select-input" value={d.testKind ?? 'positive'} onChange={(e) => set('testKind', e.target.value)}>
              <option value="positive">Positive</option>
              <option value="negative">Negative</option>
              <option value="boundary">Boundary</option>
              <option value="permission">Permission</option>
              <option value="validation">Validation</option>
            </select>
          </label>
        </>
      );
    }
    default:
      // ClarificationQuestion: no content edit form - answered/resolved through its own workflow.
      return null;
  }
}
