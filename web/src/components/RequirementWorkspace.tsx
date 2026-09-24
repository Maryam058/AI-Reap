import { useEffect, useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { phasePath } from '../lib/sdlc';
import { useAuth } from '../auth/AuthContext';
import { apiErrorText } from '../lib/errors';
import { requirementSourcesApi } from '../api/requirementSources';
import { artifactsApi } from '../api/artifacts';
import {
  ARTIFACT_TYPE_VALUES,
  type AnalyzeRequirementResult,
  type ArtifactSummary,
  type QualityFinding,
  type RequirementSource,
} from '../api/types';
import { ClarificationQuestionCard } from './ClarificationQuestionCard';
import { ArtifactCard } from './ArtifactCard';
import { EmptyState } from './ui/EmptyState';
import { IconAlert, IconCheckCircle, IconDocument, IconInbox, IconLock, IconSparkles, IconUpload } from './icons';

/** Which SDLC phase's slice of the requirement pipeline to render. Each phase page mounts its own view. */
export type WorkspaceView = 'gathering' | 'analysis' | 'validation' | 'design' | 'development' | 'testing';

export function RequirementWorkspace({
  projectId,
  view,
  onChange,
}: {
  projectId: string;
  view: WorkspaceView;
  onChange?: () => void;
}) {
  const { token, hasRole } = useAuth();
  const canWrite = hasRole('Administrator') || hasRole('BusinessAnalyst');
  const canReview = canWrite || hasRole('Reviewer');
  // §4 — QA generates and manages test cases (the API allows QA to edit TestCase artifacts only).
  const canManageTests = canWrite || hasRole('QA');

  const [sources, setSources] = useState<RequirementSource[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [newText, setNewText] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [uploadFile, setUploadFile] = useState<File | null>(null);

  const [analysis, setAnalysis] = useState<AnalyzeRequirementResult | null>(null);
  const [questions, setQuestions] = useState<ArtifactSummary[]>([]);
  const [artifacts, setArtifacts] = useState<ArtifactSummary[]>([]);
  const [qualityFindings, setQualityFindings] = useState<QualityFinding[] | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const selectedSource = sources.find((s) => s.id === selectedId) ?? null;

  const loadSources = async () => {
    try {
      const list = await requirementSourcesApi.listForProject(projectId, token);
      setSources(list);
      if (!selectedId && list.length > 0) {
        setSelectedId(list[0].id);
      }
    } catch (err) {
      setError(apiErrorText(err, 'Failed to load requirement sources.'));
    }
  };

  const loadDerived = async (sourceId: string) => {
    try {
      const [q, a] = await Promise.all([
        requirementSourcesApi.getClarificationQuestions(sourceId, token),
        artifactsApi.listForProject(projectId, token, { requirementSourceId: sourceId }),
      ]);
      setQuestions(q);
      setArtifacts(a.filter((x) => x.artifactType !== ARTIFACT_TYPE_VALUES.ClarificationQuestion));
    } catch (err) {
      setError(apiErrorText(err, 'Failed to load requirement data.'));
    }
  };

  useEffect(() => {
    loadSources();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId]);

  useEffect(() => {
    setAnalysis(null);
    setQualityFindings(null);
    if (selectedId) {
      loadDerived(selectedId);
    } else {
      setQuestions([]);
      setArtifacts([]);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedId]);

  const runAction = async (key: string, fn: () => Promise<void>) => {
    setBusy(key);
    setError(null);
    try {
      await fn();
      onChange?.();
    } catch (err) {
      // AI provider/validation failures and rejected status changes carry a user-facing explanation.
      setError(apiErrorText(err, 'Action failed. Please try again.'));
    } finally {
      setBusy(null);
    }
  };

  const onCreateSource = () =>
    runAction('create-source', async () => {
      const created = await requirementSourcesApi.create(projectId, { sourceType: 1, rawText: newText }, token);
      setNewText('');
      await loadSources();
      setSelectedId(created.id);
    });

  const onUploadFile = () =>
    uploadFile &&
    runAction('upload-source', async () => {
      const created = await requirementSourcesApi.upload(projectId, uploadFile, token);
      setUploadFile(null);
      await loadSources();
      setSelectedId(created.id);
    });

  const onAnalyze = () =>
    selectedId &&
    runAction('analyze', async () => {
      const result = await requirementSourcesApi.analyze(selectedId, token);
      setAnalysis(result);
      await loadDerived(selectedId);
    });

  const onAnswer = (artifactId: string, answer: string, notApplicable: boolean) =>
    runAction(`answer-${artifactId}`, async () => {
      await artifactsApi.answerClarification(artifactId, { answer, notApplicable }, token);
      if (selectedId) await loadDerived(selectedId);
    });

  const onGenerateRequirements = () =>
    selectedId &&
    runAction('generate-requirements', async () => {
      await requirementSourcesApi.generateRequirements(selectedId, token);
      await loadDerived(selectedId);
    });

  const onGenerateStories = () =>
    selectedId &&
    runAction('generate-stories', async () => {
      await requirementSourcesApi.generateUserStories(selectedId, token);
      await loadDerived(selectedId);
    });

  const onGenerateBusinessRules = () =>
    selectedId &&
    runAction('generate-rules', async () => {
      await requirementSourcesApi.generateBusinessRules(selectedId, token);
      await loadDerived(selectedId);
    });

  const onAnalyzeQuality = () =>
    selectedId &&
    runAction('analyze-quality', async () => {
      const findings = await requirementSourcesApi.analyzeQuality(selectedId, token);
      setQualityFindings(findings);
    });

  const onGenerateAcceptanceCriteria = (userStoryId: string) =>
    runAction(`generate-ac-${userStoryId}`, async () => {
      await artifactsApi.generateAcceptanceCriteria(userStoryId, token);
      if (selectedId) await loadDerived(selectedId);
    });

  const onGenerateTestCases = (functionalRequirementId: string) =>
    runAction(`generate-tc-${functionalRequirementId}`, async () => {
      await artifactsApi.generateTestCases(functionalRequirementId, token);
      if (selectedId) await loadDerived(selectedId);
    });

  const onGenerateDesign = () =>
    selectedId &&
    runAction('generate-design', async () => {
      await requirementSourcesApi.generateDesign(selectedId, token);
      await loadDerived(selectedId);
    });

  const onGenerateDataEntities = () =>
    selectedId &&
    runAction('generate-data-entities', async () => {
      await requirementSourcesApi.generateDataEntities(selectedId, token);
      await loadDerived(selectedId);
    });

  const onGenerateApiSpecs = () =>
    selectedId &&
    runAction('generate-api-specs', async () => {
      await requirementSourcesApi.generateApiSpecs(selectedId, token);
      await loadDerived(selectedId);
    });

  const onGenerateTasks = () =>
    selectedId &&
    runAction('generate-tasks', async () => {
      await requirementSourcesApi.generateTasks(selectedId, token);
      await loadDerived(selectedId);
    });

  const onApprove = (id: string) =>
    runAction(`approve-${id}`, async () => {
      await artifactsApi.updateStatus(id, { status: 3 }, token); // 3 = Approved
      if (selectedId) await loadDerived(selectedId);
    });

  const onReject = (id: string) =>
    runAction(`reject-${id}`, async () => {
      await artifactsApi.updateStatus(id, { status: 4 }, token); // 4 = Rejected
      if (selectedId) await loadDerived(selectedId);
    });

  // §24 workflow moves other than approve/reject (submit for review, return to draft, reopen).
  const onChangeStatus = (id: string, status: number) =>
    runAction(`status-${id}`, async () => {
      await artifactsApi.updateStatus(id, { status }, token);
      if (selectedId) await loadDerived(selectedId);
    });

  const functionalRequirements = artifacts.filter((a) => a.artifactType === ARTIFACT_TYPE_VALUES.FunctionalRequirement);
  const nonFunctionalRequirements = artifacts.filter((a) => a.artifactType === ARTIFACT_TYPE_VALUES.NonFunctionalRequirement);
  const businessRules = artifacts.filter((a) => a.artifactType === ARTIFACT_TYPE_VALUES.BusinessRule);
  const userStories = artifacts.filter((a) => a.artifactType === ARTIFACT_TYPE_VALUES.UserStory);
  const acceptanceCriteria = artifacts.filter((a) => a.artifactType === ARTIFACT_TYPE_VALUES.AcceptanceCriterion);
  const designArtifacts = artifacts.filter((a) => a.artifactType === ARTIFACT_TYPE_VALUES.DesignArtifact);
  const dataEntities = artifacts.filter((a) => a.artifactType === ARTIFACT_TYPE_VALUES.DataEntity);
  const apiSpecifications = artifacts.filter((a) => a.artifactType === ARTIFACT_TYPE_VALUES.ApiSpecification);
  const implementationTasks = artifacts.filter((a) => a.artifactType === ARTIFACT_TYPE_VALUES.ImplementationTask);
  const testCases = artifacts.filter((a) => a.artifactType === ARTIFACT_TYPE_VALUES.TestCase);

  const openQuestionCount = questions.filter((q) => (q.data as { clarificationStatus?: string }).clarificationStatus === 'Open').length;

  const needsFr = functionalRequirements.length === 0;
  const needsFrHint = needsFr ? 'Generate functional requirements first' : undefined;

  const isGathering = view === 'gathering';
  const phaseArtifactCount =
    view === 'analysis'
      ? functionalRequirements.length + nonFunctionalRequirements.length + businessRules.length + userStories.length + acceptanceCriteria.length
      : view === 'design'
        ? designArtifacts.length + dataEntities.length + apiSpecifications.length
        : view === 'development'
          ? implementationTasks.length
          : view === 'testing'
            ? testCases.length
            : 0;

  const onArtifactSaved = () => {
    if (selectedId) loadDerived(selectedId);
  };

  const reviewProps = { canReview, canEdit: canWrite, token, onApprove, onReject, onChangeStatus, onSaved: onArtifactSaved };
  const generateButton = (key: string, label: string, busyLabel: string, run: () => unknown, disabled: boolean, primary = false, hint?: string) => (
    <button
      type="button"
      className={`btn ${primary ? 'btn-primary' : 'btn-secondary'} btn-sm`}
      disabled={busy === key || disabled}
      onClick={run}
      title={hint}
    >
      {primary && <IconSparkles width={14} height={14} />}
      {busy === key ? busyLabel : label}
    </button>
  );

  return (
    <div className="req">
      {error && (
        <div className="alert alert-danger req-alert" role="alert">
          <IconAlert width={16} height={16} />
          <span>{error}</span>
        </div>
      )}

      {isGathering && canWrite && (
        <div className="req-intake">
          {/* Plain textarea, not a rich-text editor — intentional (PDF §6), not an oversight.
              This raw text is sent to the LLM as a flat string either way, so WYSIWYG
              formatting would be stored and displayed but never consumed by the pipeline. */}
          <label className="field">
            <span className="field-label">Raw requirement / meeting notes</span>
            <textarea
              className="req-textarea"
              value={newText}
              onChange={(e) => setNewText(e.target.value)}
              rows={4}
              placeholder="Paste stakeholder notes, an email thread, or a rough brief…"
            />
          </label>
          <div className="req-intake-actions">
            <button
              type="button"
              className="btn btn-primary"
              disabled={!newText.trim() || busy === 'create-source'}
              onClick={onCreateSource}
            >
              {busy === 'create-source' ? 'Saving…' : 'Add requirement source'}
            </button>

            <span className="req-or">or</span>

            <div className="req-upload">
              <label className="btn btn-secondary req-file-btn">
                <IconUpload width={15} height={15} />
                {uploadFile ? 'Change file' : 'Choose file'}
                <input
                  type="file"
                  accept=".txt,.pdf,.docx"
                  onChange={(e) => setUploadFile(e.target.files?.[0] ?? null)}
                />
              </label>
              <span className="req-file-name" title={uploadFile?.name}>
                {uploadFile ? uploadFile.name : '.txt, .pdf or .docx'}
              </span>
              <button
                type="button"
                className="btn btn-secondary"
                disabled={!uploadFile || busy === 'upload-source'}
                onClick={onUploadFile}
              >
                {busy === 'upload-source' ? 'Uploading…' : 'Upload'}
              </button>
            </div>
          </div>
        </div>
      )}

      {sources.length === 0 && (
        <EmptyState
          icon={<IconInbox width={20} height={20} />}
          title={isGathering ? 'No requirement sources yet' : 'No requirements yet'}
          description={
            isGathering
              ? canWrite
                ? 'Paste notes or upload a document above. Every later phase — analysis, validation, design — starts from this raw input.'
                : 'A business analyst needs to add a requirement source before anything appears here.'
              : 'Start by capturing the business needs for this project. This phase works on the requirement sources added during Requirements Gathering.'
          }
          action={
            !isGathering && (
              <Link to={phasePath(projectId, 'gathering')} className="btn btn-primary">
                Start Requirements Gathering
              </Link>
            )
          }
        />
      )}

      {sources.length > 0 && (
        <div className="req-sources" role="tablist" aria-label="Requirement sources">
          <span className="req-sources-label">Source</span>
          {sources.map((s) => (
            <button
              key={s.id}
              type="button"
              role="tab"
              aria-selected={s.id === selectedId}
              className={`req-source ${s.id === selectedId ? 'active' : ''}`}
              onClick={() => setSelectedId(s.id)}
              title={s.rawText}
            >
              {s.originalFileName ? <IconUpload width={13} height={13} /> : <IconDocument width={13} height={13} />}
              <span>
                {(s.originalFileName ?? s.rawText).slice(0, 40)}
                {(s.originalFileName ?? s.rawText).length > 40 ? '…' : ''}
              </span>
            </button>
          ))}
        </div>
      )}

      {selectedSource && (
        <div className="req-detail">
          {isGathering && (
            <div className="req-original">
              <div className="req-original-head">
                <IconLock width={13} height={13} />
                <span>Original input</span>
                <span className="req-original-note">Never overwritten by AI output</span>
              </div>
              <pre>{selectedSource.rawText}</pre>
            </div>
          )}

          {isGathering && sources.length > 0 && (
            <p className="req-next-hint">
              Input captured. <Link to={phasePath(projectId, 'analysis')}>Continue to Requirements Analysis →</Link>
            </p>
          )}

          {canWrite && view === 'analysis' && (
            <div className="req-pipeline" id="sec-ai-analysis">
              <PipelineStep n={1} title="Analyze">
                {generateButton('analyze', 'Run AI analysis', 'Analyzing…', onAnalyze, false, true)}
              </PipelineStep>
              <PipelineStep n={2} title="Specify">
                {generateButton('generate-requirements', 'Generate FR / NFR', 'Generating…', onGenerateRequirements, questions.length === 0, false, questions.length === 0 ? 'Run AI analysis first' : undefined)}
                {generateButton('generate-stories', 'Generate user stories', 'Generating…', onGenerateStories, needsFr, false, needsFrHint)}
                {generateButton('generate-rules', 'Extract business rules', 'Generating…', onGenerateBusinessRules, needsFr, false, needsFrHint)}
              </PipelineStep>
            </div>
          )}

          {canWrite && view === 'validation' && (
            <div className="req-pipeline">
              <PipelineStep n={1} title="Quality check">
                {generateButton('analyze-quality', 'Analyze quality', 'Analyzing…', onAnalyzeQuality, needsFr, true, needsFrHint)}
              </PipelineStep>
            </div>
          )}

          {canWrite && view === 'design' && (
            <div className="req-pipeline">
              <PipelineStep n={1} title="Generate design">
                {generateButton('generate-design', 'Solution design', 'Generating…', onGenerateDesign, needsFr, true, needsFrHint)}
                {generateButton('generate-data-entities', 'Data entities', 'Generating…', onGenerateDataEntities, needsFr, false, needsFrHint)}
                {generateButton('generate-api-specs', 'API specs', 'Generating…', onGenerateApiSpecs, needsFr, false, needsFrHint)}
              </PipelineStep>
            </div>
          )}

          {canWrite && view === 'development' && (
            <div className="req-pipeline">
              <PipelineStep n={1} title="Plan implementation">
                {generateButton('generate-tasks', 'Implementation tasks', 'Generating…', onGenerateTasks, needsFr, true, needsFrHint)}
              </PipelineStep>
            </div>
          )}

          {view === 'analysis' && analysis && (
            <div className="req-ai-panel">
              <div className="req-ai-panel-head">
                <span className="ai-badge">
                  <IconSparkles width={12} height={12} /> AI Generated — Human Review Required
                </span>
                <h3>Analysis summary</h3>
              </div>
              <dl className="req-summary">
                <SummaryRow label="Actors" items={analysis.actors} />
                <SummaryRow label="Capabilities" items={analysis.capabilities} />
                <SummaryRow label="Data elements" items={analysis.dataElements} />
              </dl>
              {analysis.notes.length > 0 && (
                <p className="req-notes">
                  <span className="detail-label">Notes</span>
                  {analysis.notes.join(' ')}
                </p>
              )}
            </div>
          )}

          {view === 'analysis' && openQuestionCount > 0 && (
            <p className="req-next-hint warn">
              {openQuestionCount} clarification {openQuestionCount === 1 ? 'question is' : 'questions are'} still open.{' '}
              <Link to={phasePath(projectId, 'validation')}>Resolve them in Requirements Validation →</Link>
            </p>
          )}

          {view === 'validation' && qualityFindings && (
            <div className="req-ai-panel" id="sec-quality">
              <div className="req-ai-panel-head">
                <span className="ai-badge">
                  <IconSparkles width={12} height={12} /> AI Generated — Human Review Required
                </span>
                <h3>Quality analysis</h3>
              </div>
              {qualityFindings.length === 0 ? (
                <p className="req-all-clear">
                  <IconCheckCircle width={16} height={16} /> No quality issues found.
                </p>
              ) : (
                <ul className="req-findings">
                  {qualityFindings.map((f, i) => (
                    <li key={i}>
                      <div className="req-finding-title">
                        <span className="code">{f.artifactCode}</span>
                        <strong>{f.artifactTitle}</strong>
                      </div>
                      <p className="req-finding-issue">
                        <span className="detail-label">Issue</span>
                        {f.issue}
                      </p>
                      <p className="req-finding-fix">
                        <span className="detail-label">Recommendation</span>
                        {f.recommendation}
                      </p>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}

          {view === 'validation' && questions.length === 0 && (
            <EmptyState
              compact
              icon={<IconCheckCircle width={18} height={18} />}
              title="No clarification questions"
              description="Questions appear here after Requirements Analysis flags missing or ambiguous information."
            />
          )}

          {view === 'validation' && questions.length > 0 && (
            <section className="req-section" id="sec-clarifications">
              <div className="req-section-head">
                <h3>Clarification questions</h3>
                <span className="req-count">{questions.length}</span>
                {openQuestionCount > 0 && <span className="status-pill tone-warning">{openQuestionCount} open</span>}
              </div>
              <ul className="cq-list">
                {questions.map((q) => (
                  <ClarificationQuestionCard key={q.id} artifact={q} canAnswer={canWrite} onAnswer={onAnswer} />
                ))}
              </ul>
            </section>
          )}

          {view === 'analysis' && (
            <>
              {phaseArtifactCount === 0 && (
                <EmptyState
                  compact
                  icon={<IconDocument width={18} height={18} />}
                  title="No structured requirements yet"
                  description={canWrite ? 'Run AI analysis, then generate functional and non-functional requirements from this source.' : 'A business analyst has not generated requirements from this source yet.'}
                />
              )}
              {functionalRequirements.length > 0 && (
                <ArtifactSection id="sec-functional" title="Functional Requirements" items={functionalRequirements} {...reviewProps} />
              )}
              {nonFunctionalRequirements.length > 0 && (
                <ArtifactSection id="sec-nonfunctional" title="Non-Functional Requirements" items={nonFunctionalRequirements} {...reviewProps} />
              )}
              {businessRules.length > 0 && <ArtifactSection title="Business Rules" items={businessRules} {...reviewProps} />}
              {userStories.length > 0 && (
                <ArtifactSection title="User Stories" items={userStories} {...reviewProps} onGenerateAcceptanceCriteria={onGenerateAcceptanceCriteria} />
              )}
              {acceptanceCriteria.length > 0 && <ArtifactSection title="Acceptance Criteria" items={acceptanceCriteria} {...reviewProps} />}
            </>
          )}

          {view === 'design' && (
            <>
              {phaseArtifactCount === 0 && (
                <EmptyState
                  compact
                  icon={<IconInbox width={18} height={18} />}
                  title="No design artifacts yet"
                  description={needsFr ? 'Generate functional requirements in Requirements Analysis first — design is derived from them.' : 'Generate a solution design, data entities and API specifications from the requirements.'}
                />
              )}
              {designArtifacts.length > 0 && <ArtifactSection id="sec-architecture" title="Solution Design" items={designArtifacts} {...reviewProps} />}
              {dataEntities.length > 0 && <ArtifactSection id="sec-data" title="Data Entities" items={dataEntities} {...reviewProps} />}
              {apiSpecifications.length > 0 && <ArtifactSection id="sec-api" title="API Specifications" items={apiSpecifications} {...reviewProps} />}
            </>
          )}

          {view === 'development' && (
            <>
              {phaseArtifactCount === 0 && (
                <EmptyState
                  compact
                  icon={<IconInbox width={18} height={18} />}
                  title="No implementation tasks yet"
                  description={needsFr ? 'Generate functional requirements first — tasks are derived from them.' : 'Generate implementation tasks from the requirements and design.'}
                />
              )}
              {implementationTasks.length > 0 && <ArtifactSection id="sec-tasks" title="Implementation Tasks" items={implementationTasks} {...reviewProps} />}
            </>
          )}

          {view === 'testing' && (
            <>
              {canManageTests && functionalRequirements.length > 0 && (
                <section className="req-section">
                  <div className="req-section-head">
                    <h3>Generate test cases per requirement</h3>
                    <span className="req-count">{functionalRequirements.length}</span>
                  </div>
                  <ul className="tc-source-list">
                    {functionalRequirements.map((fr) => {
                      return (
                        <li key={fr.id}>
                          <span className="code">{fr.code}</span>
                          <span className="tc-source-title">{fr.title}</span>
                          <button
                            type="button"
                            className="btn btn-secondary btn-sm"
                            disabled={busy === `generate-tc-${fr.id}`}
                            onClick={() => onGenerateTestCases(fr.id)}
                          >
                            <IconSparkles width={14} height={14} />
                            {busy === `generate-tc-${fr.id}` ? 'Generating…' : 'Generate test cases'}
                          </button>
                        </li>
                      );
                    })}
                  </ul>
                </section>
              )}
              {phaseArtifactCount === 0 && (
                <EmptyState
                  compact
                  icon={<IconInbox width={18} height={18} />}
                  title="No test cases yet"
                  description={needsFr ? 'Generate functional requirements first — test cases are derived from them.' : 'Generate test cases from a functional requirement above.'}
                />
              )}
              {testCases.length > 0 && (
                <ArtifactSection id="sec-tests" title="Test Cases" items={testCases} {...reviewProps} canEdit={canManageTests} />
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}

function PipelineStep({ n, title, children }: { n: number; title: string; children: ReactNode }) {
  return (
    <div className="req-step">
      <div className="req-step-head">
        <span className="req-step-num">{n}</span>
        <span className="req-step-title">{title}</span>
      </div>
      <div className="req-step-actions">{children}</div>
    </div>
  );
}

function SummaryRow({ label, items }: { label: string; items: string[] }) {
  return (
    <div className="req-summary-row">
      <dt>{label}</dt>
      <dd>
        {items.length === 0 ? (
          <span className="req-none">—</span>
        ) : (
          items.map((item, i) => (
            <span key={i} className="req-chip">
              {item}
            </span>
          ))
        )}
      </dd>
    </div>
  );
}

function ArtifactSection({
  id,
  title,
  items,
  canReview,
  canEdit,
  token,
  onApprove,
  onReject,
  onChangeStatus,
  onSaved,
  onGenerateAcceptanceCriteria,
  onGenerateTestCases,
}: {
  id?: string;
  title: string;
  items: ArtifactSummary[];
  canReview: boolean;
  canEdit: boolean;
  token: string | null;
  onApprove: (id: string) => Promise<void>;
  onReject: (id: string) => Promise<void>;
  onChangeStatus: (id: string, status: number) => Promise<void>;
  onSaved: () => void;
  onGenerateAcceptanceCriteria?: (id: string) => Promise<void>;
  onGenerateTestCases?: (id: string) => Promise<void>;
}) {
  return (
    <section className="req-section" id={id}>
      <div className="req-section-head">
        <h3>{title}</h3>
        <span className="req-count">{items.length}</span>
      </div>
      <ul className="artifact-list">
        {items.map((a) => (
          <ArtifactCard
            key={a.id}
            artifact={a}
            canReview={canReview}
            canEdit={canEdit}
            token={token}
            onApprove={onApprove}
            onReject={onReject}
            onChangeStatus={onChangeStatus}
            onSaved={onSaved}
            onGenerateAcceptanceCriteria={onGenerateAcceptanceCriteria}
            onGenerateTestCases={onGenerateTestCases}
          />
        ))}
      </ul>
    </section>
  );
}
