import { useEffect, useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { ApiError } from '../api/client';
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

export function RequirementWorkspace({ projectId, onChange }: { projectId: string; onChange?: () => void }) {
  const { token, hasRole } = useAuth();
  const canWrite = hasRole('Administrator') || hasRole('BusinessAnalyst');
  const canReview = canWrite || hasRole('Reviewer');

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
      setError(err instanceof ApiError ? err.message : 'Failed to load requirement sources.');
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
      setError(err instanceof ApiError ? err.message : 'Failed to load requirement data.');
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
      setError(err instanceof ApiError ? err.message : 'Action failed.');
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

  return (
    <div className="card">
      <h2>Requirements</h2>

      {canWrite && (
        <div className="source-form">
          <label>
            Paste raw requirement / meeting notes
            <textarea value={newText} onChange={(e) => setNewText(e.target.value)} rows={3} />
          </label>
          <button type="button" disabled={!newText.trim() || busy === 'create-source'} onClick={onCreateSource}>
            {busy === 'create-source' ? 'Saving…' : 'Add requirement source'}
          </button>

          <div className="upload-row">
            <span className="hint">or upload a .txt, .pdf, or .docx file:</span>
            <input type="file" accept=".txt,.pdf,.docx" onChange={(e) => setUploadFile(e.target.files?.[0] ?? null)} />
            <button type="button" disabled={!uploadFile || busy === 'upload-source'} onClick={onUploadFile}>
              {busy === 'upload-source' ? 'Uploading…' : 'Upload'}
            </button>
          </div>
        </div>
      )}

      {sources.length > 0 && (
        <div className="source-tabs">
          {sources.map((s) => (
            <button
              key={s.id}
              type="button"
              className={`tab ${s.id === selectedId ? 'active' : ''}`}
              onClick={() => setSelectedId(s.id)}
            >
              {s.rawText.slice(0, 40)}
              {s.rawText.length > 40 ? '…' : ''}
            </button>
          ))}
        </div>
      )}

      {error && <p className="error">{error}</p>}

      {selectedSource && (
        <div className="source-detail">
          <p className="hint">Original input (never overwritten by AI output):</p>
          <pre>{selectedSource.rawText}</pre>

          {canWrite && (
            <div className="pipeline-actions">
              <button type="button" disabled={busy === 'analyze'} onClick={onAnalyze}>
                {busy === 'analyze' ? 'Analyzing…' : 'Run AI analysis'}
              </button>
              <button
                type="button"
                disabled={busy === 'generate-requirements' || questions.length === 0}
                onClick={onGenerateRequirements}
                title={questions.length === 0 ? 'Run AI analysis first' : undefined}
              >
                {busy === 'generate-requirements' ? 'Generating…' : 'Generate FR / NFR'}
              </button>
              <button
                type="button"
                disabled={busy === 'generate-stories' || functionalRequirements.length === 0}
                onClick={onGenerateStories}
                title={functionalRequirements.length === 0 ? 'Generate functional requirements first' : undefined}
              >
                {busy === 'generate-stories' ? 'Generating…' : 'Generate user stories'}
              </button>
              <button
                type="button"
                disabled={busy === 'generate-rules' || functionalRequirements.length === 0}
                onClick={onGenerateBusinessRules}
                title={functionalRequirements.length === 0 ? 'Generate functional requirements first' : undefined}
              >
                {busy === 'generate-rules' ? 'Generating…' : 'Extract business rules'}
              </button>
              <button
                type="button"
                className="secondary"
                disabled={busy === 'analyze-quality' || functionalRequirements.length === 0}
                onClick={onAnalyzeQuality}
                title={functionalRequirements.length === 0 ? 'Generate functional requirements first' : undefined}
              >
                {busy === 'analyze-quality' ? 'Analyzing…' : 'Analyze quality'}
              </button>
              <button
                type="button"
                disabled={busy === 'generate-design' || functionalRequirements.length === 0}
                onClick={onGenerateDesign}
                title={functionalRequirements.length === 0 ? 'Generate functional requirements first' : undefined}
              >
                {busy === 'generate-design' ? 'Generating…' : 'Generate solution design'}
              </button>
              <button
                type="button"
                disabled={busy === 'generate-data-entities' || functionalRequirements.length === 0}
                onClick={onGenerateDataEntities}
                title={functionalRequirements.length === 0 ? 'Generate functional requirements first' : undefined}
              >
                {busy === 'generate-data-entities' ? 'Generating…' : 'Generate data entities'}
              </button>
              <button
                type="button"
                disabled={busy === 'generate-api-specs' || functionalRequirements.length === 0}
                onClick={onGenerateApiSpecs}
                title={functionalRequirements.length === 0 ? 'Generate functional requirements first' : undefined}
              >
                {busy === 'generate-api-specs' ? 'Generating…' : 'Generate API specs'}
              </button>
              <button
                type="button"
                disabled={busy === 'generate-tasks' || functionalRequirements.length === 0}
                onClick={onGenerateTasks}
                title={functionalRequirements.length === 0 ? 'Generate functional requirements first' : undefined}
              >
                {busy === 'generate-tasks' ? 'Generating…' : 'Generate implementation tasks'}
              </button>
            </div>
          )}

          {qualityFindings && (
            <div className="analysis-result quality-result">
              {qualityFindings.length === 0 ? (
                <p>No quality issues found.</p>
              ) : (
                <>
                  <span className="ai-badge">AI Generated — Human Review Required</span>
                  <ul className="quality-findings">
                    {qualityFindings.map((f, i) => (
                      <li key={i}>
                        <span className="code">{f.artifactCode}</span> {f.artifactTitle}
                        <p className="issue">
                          <strong>Issue:</strong> {f.issue}
                        </p>
                        <p className="recommendation">
                          <strong>Recommendation:</strong> {f.recommendation}
                        </p>
                      </li>
                    ))}
                  </ul>
                </>
              )}
            </div>
          )}

          {analysis && (
            <div className="analysis-result">
              <span className="ai-badge">AI Generated — Human Review Required</span>
              <p>
                <strong>Actors:</strong> {analysis.actors.join(', ') || '—'}
              </p>
              <p>
                <strong>Capabilities:</strong> {analysis.capabilities.join(', ') || '—'}
              </p>
              <p>
                <strong>Data elements:</strong> {analysis.dataElements.join(', ') || '—'}
              </p>
              {analysis.notes.length > 0 && (
                <p>
                  <strong>Notes:</strong> {analysis.notes.join(' ')}
                </p>
              )}
            </div>
          )}

          {questions.length > 0 && (
            <section>
              <h3>
                Clarification questions {openQuestionCount > 0 && <span className="open-count">({openQuestionCount} open)</span>}
              </h3>
              <ul className="cq-list">
                {questions.map((q) => (
                  <ClarificationQuestionCard key={q.id} artifact={q} canAnswer={canWrite} onAnswer={onAnswer} />
                ))}
              </ul>
            </section>
          )}

          {functionalRequirements.length > 0 && (
            <ArtifactSection
              title="Functional Requirements"
              items={functionalRequirements}
              canReview={canReview}
              onApprove={onApprove}
              onReject={onReject}
              onGenerateTestCases={onGenerateTestCases}
            />
          )}
          {nonFunctionalRequirements.length > 0 && (
            <ArtifactSection title="Non-Functional Requirements" items={nonFunctionalRequirements} canReview={canReview} onApprove={onApprove} onReject={onReject} />
          )}
          {businessRules.length > 0 && (
            <ArtifactSection title="Business Rules" items={businessRules} canReview={canReview} onApprove={onApprove} onReject={onReject} />
          )}
          {userStories.length > 0 && (
            <ArtifactSection
              title="User Stories"
              items={userStories}
              canReview={canReview}
              onApprove={onApprove}
              onReject={onReject}
              onGenerateAcceptanceCriteria={onGenerateAcceptanceCriteria}
            />
          )}
          {acceptanceCriteria.length > 0 && (
            <ArtifactSection title="Acceptance Criteria" items={acceptanceCriteria} canReview={canReview} onApprove={onApprove} onReject={onReject} />
          )}
          {designArtifacts.length > 0 && (
            <ArtifactSection title="Solution Design" items={designArtifacts} canReview={canReview} onApprove={onApprove} onReject={onReject} />
          )}
          {dataEntities.length > 0 && (
            <ArtifactSection title="Data Entities" items={dataEntities} canReview={canReview} onApprove={onApprove} onReject={onReject} />
          )}
          {apiSpecifications.length > 0 && (
            <ArtifactSection title="API Specifications" items={apiSpecifications} canReview={canReview} onApprove={onApprove} onReject={onReject} />
          )}
          {implementationTasks.length > 0 && (
            <ArtifactSection title="Implementation Tasks" items={implementationTasks} canReview={canReview} onApprove={onApprove} onReject={onReject} />
          )}
          {testCases.length > 0 && (
            <ArtifactSection title="Test Cases" items={testCases} canReview={canReview} onApprove={onApprove} onReject={onReject} />
          )}
        </div>
      )}
    </div>
  );
}

function ArtifactSection({
  title,
  items,
  canReview,
  onApprove,
  onReject,
  onGenerateAcceptanceCriteria,
  onGenerateTestCases,
}: {
  title: string;
  items: ArtifactSummary[];
  canReview: boolean;
  onApprove: (id: string) => Promise<void>;
  onReject: (id: string) => Promise<void>;
  onGenerateAcceptanceCriteria?: (id: string) => Promise<void>;
  onGenerateTestCases?: (id: string) => Promise<void>;
}) {
  return (
    <section>
      <h3>{title}</h3>
      <ul className="artifact-list">
        {items.map((a) => (
          <ArtifactCard
            key={a.id}
            artifact={a}
            canReview={canReview}
            onApprove={onApprove}
            onReject={onReject}
            onGenerateAcceptanceCriteria={onGenerateAcceptanceCriteria}
            onGenerateTestCases={onGenerateTestCases}
          />
        ))}
      </ul>
    </section>
  );
}
