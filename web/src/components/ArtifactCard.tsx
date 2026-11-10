import { useEffect, useRef, useState } from 'react';
import { ArtifactDetails } from './ArtifactDetails';
import { ArtifactEditDialog } from './ArtifactEditDialog';
import { WorkRecordPanel } from './WorkRecordPanel';
import { DesignReviewPanel } from './DesignReviewPanel';
import { IconCheck, IconChevronDown, IconEdit, IconSparkles, IconX } from './icons';
import { artifactsApi } from '../api/artifacts';
import { useAuth } from '../auth/AuthContext';
import { apiErrorText } from '../lib/errors';
import { useDisclosure, type DisclosureRequest } from '../lib/disclosure';
import { testCaseReviewNote } from '../lib/testCaseReview';
import { canGenerateDownstream } from '../lib/downstreamGeneration';
import { evidenceSummary, originLabel, quoteLabel, targetLabel, unsupportedNumbersLabel, type EvidenceLabel } from '../lib/evidence';
import {
  ARTIFACT_PRIORITY_LABELS,
  ARTIFACT_STATUS,
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
  type RequirementEvidenceData,
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
  // §24 — review decisions; the comment is recorded with the decision (required, as the reason, to
  // reject) and made on `version`, the version shown (S1). Resolve to whether the decision was recorded.
  onApprove: (id: string, comment: string | undefined, version: number) => Promise<boolean>;
  onReject: (id: string, comment: string | undefined, version: number) => Promise<boolean>;
  // §24 — the other human workflow moves (submit for review, return to draft, reopen review).
  onChangeStatus?: (id: string, status: number) => Promise<void>;
  onSaved: () => void;
  onGenerateAcceptanceCriteria?: (userStoryId: string) => Promise<void>;
  onGenerateTestCases?: (functionalRequirementId: string) => Promise<void>;
  // Compact mode for generated collections: header and a one-line preview until expanded. Actions and
  // review are unchanged once the card is open.
  collapsible?: boolean;
  /** Open this card (a guidance link's ?focus= target). */
  reveal?: DisclosureRequest | null;
  /** Scroll to and briefly highlight this card. */
  focused?: boolean;
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
  onChangeStatus,
  onSaved,
  onGenerateAcceptanceCriteria,
  onGenerateTestCases,
  collapsible = false,
  reveal = null,
  focused = false,
}: Props) {
  const [busy, setBusy] = useState(false);
  const [editing, setEditing] = useState(false);
  const [reviewComment, setReviewComment] = useState('');
  const [reviewError, setReviewError] = useState<string | null>(null);
  const [reviewsRecorded, setReviewsRecorded] = useState(0);
  const [confirmError, setConfirmError] = useState<string | null>(null);
  const { userId, hasRole } = useAuth();

  // Mirrors the backend state machine (ArtifactStatusTransitions): a reviewer decides on AI-generated
  // or under-review content; a draft must be submitted first; approved-or-later work can be reopened.
  const status = artifact.status;
  const canDecide = status === ARTIFACT_STATUS.AiGenerated || status === ARTIFACT_STATUS.UnderReview;
  const canSubmit = status === ARTIFACT_STATUS.Draft || status === ARTIFACT_STATUS.Rejected;
  const canReturnToDraft = status === ARTIFACT_STATUS.UnderReview || status === ARTIFACT_STATUS.Rejected;
  const isApprovedOrLater =
    status === ARTIFACT_STATUS.Approved || status === ARTIFACT_STATUS.Implemented || status === ARTIFACT_STATUS.Verified;
  const editedAfterApproval =
    status === ARTIFACT_STATUS.UnderReview && artifact.approvedVersion != null && artifact.approvedVersion < artifact.currentVersion;
  // Explains why a Draft test case offers Submit for review rather than Approve/Reject (text only).
  const reviewNote = artifact.artifactType === ARTIFACT_TYPE_VALUES.TestCase ? testCaseReviewNote(status) : null;
  const openNotices = artifact.openImpactNoticeCount ?? 0;
  const isAiOrigin = artifact.origin === 0;
  const isEditable = canEdit && artifact.artifactType !== ARTIFACT_TYPE_VALUES.ClarificationQuestion;
  // An upstream-change notice needs attention, so such a card starts open.
  const disclosure = useDisclosure(!collapsible || openNotices > 0, reveal);
  const expanded = !collapsible || disclosure.open;
  // A guidance link targeted this card: bring it into view once it has opened.
  const cardRef = useRef<HTMLLIElement>(null);
  const focusStamp = focused ? reveal?.stamp : undefined;
  useEffect(() => {
    if (focusStamp === undefined) return;
    const reduceMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
    const timer = window.setTimeout(() => cardRef.current?.scrollIntoView({ behavior: reduceMotion ? 'auto' : 'smooth', block: 'center' }), 120);
    return () => window.clearTimeout(timer);
  }, [focusStamp]);
  const preview = collapsible && !expanded ? artifactPreview(artifact) : null;

  // REAP-045 — a BA or Reviewer other than the author confirms an NFR's proposed target. Confirmation
  // is not approval: the status is untouched. Mirrors ArtifactService.ConfirmNfrTargetAsync, which
  // enforces it (including the author of the current version, which only the server knows).
  const nfr =
    artifact.artifactType === ARTIFACT_TYPE_VALUES.NonFunctionalRequirement
      ? (artifact.data as unknown as NonFunctionalRequirementData)
      : null;
  const canConfirmTarget =
    nfr !== null &&
    !!nfr.targetValue?.trim() &&
    nfr.assumptionStatus !== 'confirmed' &&
    status !== ARTIFACT_STATUS.Rejected &&
    (hasRole('BusinessAnalyst') || hasRole('Reviewer')) &&
    artifact.createdByUserId !== userId;

  const act = async (fn: (id: string) => Promise<void>) => {
    setBusy(true);
    try {
      await fn(artifact.id);
    } finally {
      setBusy(false);
    }
  };

  // §4/§24 — Reviewer (and BA/Administrator) review: comment only, or approve/reject with a comment.
  // Uses the existing endpoints: POST .../reviews for a comment, PATCH .../status for a decision.
  const trimmedComment = reviewComment.trim();

  const decide = async (approve: boolean) => {
    if (!approve && !trimmedComment) {
      setReviewError('Enter a reason to reject this artifact.');
      return;
    }
    setReviewError(null);
    setBusy(true);
    try {
      const recorded = await (approve ? onApprove : onReject)(artifact.id, trimmedComment || undefined, artifact.currentVersion);
      if (recorded) {
        setReviewComment('');
        setReviewsRecorded((n) => n + 1);
      }
    } finally {
      setBusy(false);
    }
  };

  const addComment = async () => {
    setReviewError(null);
    setBusy(true);
    try {
      await artifactsApi.addCommentReview(artifact.id, trimmedComment, token);
      setReviewComment('');
      setReviewsRecorded((n) => n + 1);
      onSaved();
    } catch (err) {
      setReviewError(apiErrorText(err, 'The comment could not be recorded.'));
    } finally {
      setBusy(false);
    }
  };

  const confirmTarget = async () => {
    setConfirmError(null);
    setBusy(true);
    try {
      await artifactsApi.confirmNfrTarget(artifact.id, artifact.currentVersion, token);
      onSaved();
    } catch (err) {
      setConfirmError(apiErrorText(err, 'The target could not be confirmed.'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <li
      ref={cardRef}
      id={`artifact-${artifact.id}`}
      className={`artifact-card tone-${STATUS_TONE[artifact.status] ?? 'neutral'}${collapsible ? ' is-collapsible' : ''}${expanded ? '' : ' is-collapsed'}${focused ? ' is-focused' : ''}`}
    >
      <div className="artifact-header">
        {collapsible ? (
          <button type="button" className="artifact-toggle" aria-expanded={expanded} onClick={disclosure.toggle}>
            <IconChevronDown width={15} height={15} className="gen-chevron" />
            <span className="code">{artifact.code}</span>
            <strong className="artifact-title">{artifact.title}</strong>
          </button>
        ) : (
          <>
            <span className="code">{artifact.code}</span>
            <strong className="artifact-title">{artifact.title}</strong>
          </>
        )}
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
          {openNotices > 0 && (
            <span className="status-pill tone-warning" title="An upstream artifact this depends on changed after approval. Review it and acknowledge the notice below.">
              Upstream changed — review
            </span>
          )}
        </span>
      </div>

      {preview && <p className="artifact-preview">{preview}</p>}

      <div className="artifact-content" hidden={!expanded}>
        {editedAfterApproval && (
          <p className="artifact-review-note">
            Edited after approval: v{artifact.currentVersion} needs re-review. The approved content is preserved as v{artifact.approvedVersion} in the history.
          </p>
        )}

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
          {canReview && onChangeStatus && canSubmit && (
            <button type="button" className="btn btn-secondary btn-sm" disabled={busy}
              onClick={() => act((id) => onChangeStatus(id, ARTIFACT_STATUS.UnderReview))}>
              Submit for review
            </button>
          )}
          {canReview && onChangeStatus && canReturnToDraft && (
            <button type="button" className="btn btn-ghost btn-sm" disabled={busy}
              onClick={() => act((id) => onChangeStatus(id, ARTIFACT_STATUS.Draft))}>
              Return to draft
            </button>
          )}
          {canReview && onChangeStatus && isApprovedOrLater && (
            <button type="button" className="btn btn-ghost btn-sm" disabled={busy}
              onClick={() => act((id) => onChangeStatus(id, ARTIFACT_STATUS.UnderReview))}>
              Reopen review
            </button>
          )}
          {canConfirmTarget && (
            <button type="button" className="btn btn-secondary btn-sm" disabled={busy} onClick={confirmTarget}
              title="Confirm this target value as a validated requirement. This does not approve the NFR.">
              <IconCheck width={14} height={14} />
              Confirm target
            </button>
          )}
          {artifact.artifactType === ARTIFACT_TYPE_VALUES.UserStory && onGenerateAcceptanceCriteria && canGenerateDownstream(artifact.status) && (
            <button type="button" className="btn btn-ghost btn-sm" disabled={busy} onClick={() => act(onGenerateAcceptanceCriteria)}>
              <IconSparkles width={14} height={14} />
              Generate acceptance criteria
            </button>
          )}
          {(artifact.artifactType === ARTIFACT_TYPE_VALUES.FunctionalRequirement || artifact.artifactType === ARTIFACT_TYPE_VALUES.NonFunctionalRequirement) &&
            onGenerateTestCases &&
            canGenerateDownstream(artifact.status) && (
            <button type="button" className="btn btn-ghost btn-sm" disabled={busy} onClick={() => act(onGenerateTestCases)}>
              <IconSparkles width={14} height={14} />
              Generate test cases
            </button>
          )}
        </div>
        {confirmError && <p className="field-error-text">{confirmError}</p>}
        {reviewNote && <p className="hint">{reviewNote}</p>}

        {canReview && (
          <div className="work-record" aria-label="Artifact review">
            <div className="work-record-head">
              <strong>Review</strong>
              <span className="muted">v{artifact.currentVersion}</span>
            </div>
            <div className="work-record-actions">
              <input
                type="text"
                placeholder={canDecide ? 'Review comment (optional to approve or comment; required to reject)' : 'Review comment'}
                value={reviewComment}
                onChange={(e) => {
                  setReviewComment(e.target.value);
                  setReviewError(null);
                }}
                disabled={busy}
                aria-label={`Review comment for ${artifact.code}`}
              />
              <button type="button" className="btn btn-ghost btn-sm" disabled={busy || !trimmedComment} onClick={addComment}>
                Comment
              </button>
              {canDecide && (
                <>
                  <button type="button" className="btn btn-primary btn-sm" disabled={busy} onClick={() => decide(true)}>
                    <IconCheck width={14} height={14} />
                    Approve
                  </button>
                  <button type="button" className="btn btn-secondary btn-sm btn-reject" disabled={busy} onClick={() => decide(false)}>
                    <IconX width={14} height={14} />
                    Reject
                  </button>
                </>
              )}
            </div>
            {reviewError && <p className="field-error-text">{reviewError}</p>}
          </div>
        )}
        {(artifact.artifactType === ARTIFACT_TYPE_VALUES.ImplementationTask || artifact.artifactType === ARTIFACT_TYPE_VALUES.TestCase) && (
          <WorkRecordPanel artifact={artifact} onRecorded={onSaved} />
        )}
        {(artifact.artifactType === ARTIFACT_TYPE_VALUES.DesignArtifact ||
          artifact.artifactType === ARTIFACT_TYPE_VALUES.ApiSpecification ||
          artifact.artifactType === ARTIFACT_TYPE_VALUES.DataEntity) && <DesignReviewPanel artifact={artifact} onRecorded={onSaved} />}

        <ArtifactDetails
          artifactId={artifact.id}
          artifactType={artifact.artifactType}
          projectId={artifact.projectId}
          openImpactNoticeCount={openNotices}
          onNoticeAcknowledged={onSaved}
          refreshKey={`${artifact.status}-${artifact.currentVersion}-${reviewsRecorded}`}
        />
      </div>

      {editing && <ArtifactEditDialog artifact={artifact} token={token} onClose={() => setEditing(false)} onSaved={onSaved} />}
    </li>
  );
}

const text = (v: unknown) => (typeof v === 'string' ? v.trim() : '');

/** One-line summary shown on a collapsed card, built only from the artifact's own payload. */
function artifactPreview(a: ArtifactSummary): string | null {
  const d = a.data;
  const join = (...parts: string[]) => parts.filter(Boolean).join(' · ') || null;
  switch (a.artifactType) {
    case ARTIFACT_TYPE_VALUES.FunctionalRequirement:
      return join(text(d.actor), text(d.expectedResult) || text(d.processing));
    case ARTIFACT_TYPE_VALUES.NonFunctionalRequirement:
      return join(text(d.targetValue), text(d.description));
    case ARTIFACT_TYPE_VALUES.UserStory:
      return text(d.persona) || text(d.valueStatement) ? `As ${text(d.persona) || '…'}: ${text(d.valueStatement)}` : null;
    case ARTIFACT_TYPE_VALUES.AcceptanceCriterion:
      return join(text(d.kind), text(d.given) && `Given ${text(d.given)}`);
    case ARTIFACT_TYPE_VALUES.BusinessRule:
      return text(d.statement) || null;
    case ARTIFACT_TYPE_VALUES.DesignArtifact:
      return text(d.architectureOverview) || null;
    case ARTIFACT_TYPE_VALUES.DataEntity: {
      const fields = Array.isArray(d.fields) ? d.fields.length : 0;
      return join(fields ? `${fields} field${fields === 1 ? '' : 's'}` : '', text(d.relationships));
    }
    case ARTIFACT_TYPE_VALUES.ApiSpecification:
      return join(`${text(d.method)} ${text(d.route)}`.trim(), text(d.purpose));
    case ARTIFACT_TYPE_VALUES.ImplementationTask:
      return join(text(d.taskType), text(d.estimate), text(d.description));
    case ARTIFACT_TYPE_VALUES.TestCase: {
      const steps = Array.isArray(d.steps) ? d.steps.length : 0;
      return join(text(d.testKind), steps ? `${steps} step${steps === 1 ? '' : 's'}` : '', text(d.expectedResult));
    }
    default:
      return null;
  }
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
      {(data.sourceReferences?.length ?? 0) > 0 && <Detail label="Sources" value={data.sourceReferences!.join(', ')} />}
      <RequirementEvidenceDetails data={data} />
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
          <span
            className={`assumption-pill assumption-${data.assumptionStatus}`}
            title={data.confirmedAt ? `Target confirmed ${new Date(data.confirmedAt).toLocaleString()}` : undefined}
          >
            {data.assumptionStatus === 'proposed_assumption' ? 'Proposed assumption — needs confirmation' : 'Target confirmed'}
          </span>
        </p>
      )}
      <RequirementEvidenceDetails data={data} target={targetLabel(data)} />
    </>
  );
}

function EvidencePill({ label }: { label: EvidenceLabel }) {
  return <span className={`status-pill tone-${label.tone}`}>{label.text}</span>;
}

// P0-2 — origin, evidence and (NFR) target labels, and the quotes with the server's result for each.
// "Found" means only that the quoted words are in the cited text. A user who may not read raw sources
// gets the results without the quote text (quoteWithheld).
function RequirementEvidenceDetails({ data, target }: { data: RequirementEvidenceData; target?: EvidenceLabel | null }) {
  const origin = originLabel(data.requirementOrigin);
  const numbers = unsupportedNumbersLabel(data.unsupportedNumbers);
  const quotes = data.evidence ?? [];
  return (
    <>
      <p className="detail-row">
        <span className="detail-label">Evidence</span>
        <span className="detail-value">
          {origin && <EvidencePill label={origin} />} <EvidencePill label={evidenceSummary(data)} /> {target && <EvidencePill label={target} />}
        </span>
      </p>
      <Detail label="Derivation" value={data.derivationNote} />
      {(data.assumedClarifications?.length ?? 0) > 0 && <Detail label="Assumes" value={data.assumedClarifications!.join(', ')} />}
      {numbers && (
        <p className="detail-row">
          <span className="detail-label">Numbers</span>
          <span className="detail-value"><span className="status-pill tone-warning">{numbers}</span></span>
        </p>
      )}
      {quotes.length > 0 && (
        <details className="work-record-history">
          <summary>Evidence quotes ({quotes.length})</summary>
          <ul>
            {quotes.map((q, i) => (
              <li key={i}>
                <EvidencePill label={quoteLabel(q)} />{' '}
                {q.quoteWithheld ? <em>Quote text withheld for your role</em> : <q>{q.quote}</q>}
              </li>
            ))}
          </ul>
        </details>
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
