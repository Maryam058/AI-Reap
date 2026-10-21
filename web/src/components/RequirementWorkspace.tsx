import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { phasePath } from '../lib/workAreas';
import { sectionForArtifactType } from '../lib/checkGuidance';
import { useAuth } from '../auth/AuthContext';
import { apiErrorText } from '../lib/errors';
import { readsRawSources } from '../lib/access';
import { planButtonHint } from '../lib/aiPlan';
import { requirementSourcesApi } from '../api/requirementSources';
import { artifactsApi } from '../api/artifacts';
import { traceabilityApi } from '../api/traceability';
import {
  ARTIFACT_TYPE_VALUES,
  type AiPlanResult,
  type AnalyzeRequirementResult,
  type ArtifactSummary,
  type ClarificationQuestionData,
  type GenerateRequirementsResult,
  type QualityFinding,
  type RequirementSource,
  type SourceIdCoverage,
  type TraceabilityRow,
} from '../api/types';
import { coverageNotice } from '../lib/sourceIdCoverage';
import { canGenerateDownstream } from '../lib/downstreamGeneration';
import { analysisCompletenessNotice, generationCompletenessNotice, type CompletenessNotice } from '../lib/completeness';
import { nextStamp, type DisclosureRequest } from '../lib/disclosure';
import { apiResourceOf, byField, groupByRequirement, groupByValue, plural, statusSummary, type ArtifactGroup } from '../lib/grouping';
import { decisionRequest } from '../lib/artifactDecision';
import { ClarificationQuestionCard } from './ClarificationQuestionCard';
import { ArtifactCard } from './ArtifactCard';
import { CollectionGroup, CollectionGroups, CollectionSection, GeneratedOverview, type OverviewItem } from './GeneratedCollection';
import { EmptyState } from './ui/EmptyState';
import { AiPlanSummary } from './AiPlanSummary';
import { AiRunPanel } from './AiRunPanel';
import { IconAlert, IconCheck, IconCheckCircle, IconChevronDown, IconDocument, IconInbox, IconLock, IconSparkles, IconUpload } from './icons';

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
  // S2 — a Developer's source list carries ids only (no text or file name); the raw input is not shown.
  const showRawInput = readsRawSources(hasRole);

  const [sources, setSources] = useState<RequirementSource[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [newText, setNewText] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [uploadFile, setUploadFile] = useState<File | null>(null);

  const [analysis, setAnalysis] = useState<AnalyzeRequirementResult | null>(null);
  // P0-7 — the last Generate response (response-only completeness; gone on reload or source change).
  const [generation, setGeneration] = useState<GenerateRequirementsResult | null>(null);
  const [questions, setQuestions] = useState<ArtifactSummary[]>([]);
  const [artifacts, setArtifacts] = useState<ArtifactSummary[]>([]);
  const [qualityFindings, setQualityFindings] = useState<QualityFinding[] | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  // Read-only traceability links, used only to group generated items by their requirement.
  const [matrix, setMatrix] = useState<TraceabilityRow[] | null>(null);
  // Requirements whose test-case generation succeeded in this session (used when links are unavailable).
  const [testsGeneratedFor, setTestsGeneratedFor] = useState<ReadonlySet<string>>(new Set());
  // Open-and-show requests: a section (overview jump) or a requirement group (after generating its tests).
  const [revealSection, setRevealSection] = useState<{ id: string; request: DisclosureRequest } | null>(null);
  const [revealGroup, setRevealGroup] = useState<{ key: string; request: DisclosureRequest } | null>(null);
  // Deep links from lifecycle guidance: ?source=<requirement source>&focus=<artifact id>, or ?section=<anchor>.
  const [params] = useSearchParams();
  const requestedSource = params.get('source');
  const requestedFocus = params.get('focus');
  const requestedSection = params.get('section');
  const [focus, setFocus] = useState<{ id: string; request: DisclosureRequest } | null>(null);
  // The source whose artifacts are currently loaded, so a deep link never acts on a previous source's list.
  const [loadedFor, setLoadedFor] = useState<string | null>(null);
  const handledLink = useRef<string | null>(null);
  // Dry-run AI plans for Analyze / Generate (estimates; no AI provider is contacted to make them).
  const [aiPlans, setAiPlans] = useState<{
    sourceId: string | null;
    analyze: AiPlanResult | null;
    generate: AiPlanResult | null;
    loading: boolean;
    failed: boolean;
  }>({ sourceId: null, analyze: null, generate: null, loading: false, failed: false });
  // Bumped after an Analyze / Generate run: a run can start a provider cool-down or add clarifications.
  const [planStamp, setPlanStamp] = useState(0);
  // P0-4 — the selected source's requirement-ID coverage, read from the server (the generate response
  // is not kept); null when it could not be read.
  const [coverage, setCoverage] = useState<{ sourceId: string | null; value: SourceIdCoverage | null }>({ sourceId: null, value: null });

  const selectedSource = sources.find((s) => s.id === selectedId) ?? null;

  const loadSources = async () => {
    try {
      const list = await requirementSourcesApi.listForProject(projectId, token);
      setSources(list);
      if (!selectedId && list.length > 0) {
        setSelectedId(list.find((s) => s.id === requestedSource)?.id ?? list[0].id);
      }
    } catch (err) {
      setError(apiErrorText(err, 'Failed to load requirement sources.'));
    }
  };

  const loadDerived = async (sourceId: string) => {
    try {
      const [q, a, m] = await Promise.all([
        requirementSourcesApi.getClarificationQuestions(sourceId, token),
        artifactsApi.listForProject(projectId, token, { requirementSourceId: sourceId }),
        // Grouping is a presentation nicety: without the links the lists simply render ungrouped.
        traceabilityApi.getMatrix(projectId, token).catch(() => null),
      ]);
      setQuestions(q);
      setArtifacts(a.filter((x) => x.artifactType !== ARTIFACT_TYPE_VALUES.ClarificationQuestion));
      setMatrix(m);
      setLoadedFor(sourceId);
    } catch (err) {
      setError(apiErrorText(err, 'Failed to load requirement data.'));
    }
  };

  useEffect(() => {
    loadSources();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId]);

  // A deep link to another source (e.g. following a second guidance link on the same page) switches to it.
  useEffect(() => {
    if (requestedSource && requestedSource !== selectedId && sources.some((s) => s.id === requestedSource)) {
      setSelectedId(requestedSource);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [requestedSource, sources]);

  useEffect(() => {
    setAnalysis(null);
    setGeneration(null);
    setQualityFindings(null);
    setLoadedFor(null);
    if (selectedId) {
      loadDerived(selectedId);
    } else {
      setQuestions([]);
      setArtifacts([]);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedId]);

  // Clarifications (added, or answered - a new version) change what Generate must carry.
  const clarificationKey = questions.map((q) => `${q.id}:${q.currentVersion}`).join(',');
  const showsAiPlan = canWrite && view === 'analysis';

  useEffect(() => {
    if (!showsAiPlan || !selectedId) return;
    let cancelled = false;
    setAiPlans((prev) =>
      prev.sourceId === selectedId
        ? { ...prev, loading: true }
        : { sourceId: selectedId, analyze: null, generate: null, loading: true, failed: false },
    );
    Promise.allSettled([
      requirementSourcesApi.aiPlan(selectedId, 'analyze', token),
      requirementSourcesApi.aiPlan(selectedId, 'generate', token),
    ]).then(([analyze, generate]) => {
      if (cancelled) return;
      setAiPlans({
        sourceId: selectedId,
        analyze: analyze.status === 'fulfilled' ? analyze.value : null,
        generate: generate.status === 'fulfilled' ? generate.value : null,
        loading: false,
        failed: analyze.status === 'rejected' || generate.status === 'rejected',
      });
    });
    return () => {
      cancelled = true;
    };
  }, [showsAiPlan, selectedId, clarificationKey, planStamp, token]);

  const plansForSelected = aiPlans.sourceId === selectedId ? aiPlans : null;

  // Resolves to whether the action succeeded, so callers only show a success state after a real success.
  const runAction = async (key: string, fn: () => Promise<void>) => {
    setBusy(key);
    setError(null);
    try {
      await fn();
      onChange?.();
      return true;
    } catch (err) {
      // AI provider/validation failures and rejected status changes carry a user-facing explanation.
      setError(apiErrorText(err, 'Action failed. Please try again.'));
      return false;
    } finally {
      setBusy(null);
      if (key === 'analyze' || key === 'generate-requirements') setPlanStamp((n) => n + 1);
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
      setGeneration(null); // a failed run must not leave the previous run's notice showing
      const result = await requirementSourcesApi.generateRequirements(selectedId, token);
      setGeneration(result);
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

  const onGenerateTestCases = async (functionalRequirementId: string) => {
    const succeeded = await runAction(`generate-tc-${functionalRequirementId}`, async () => {
      await artifactsApi.generateTestCases(functionalRequirementId, token);
      if (selectedId) await loadDerived(selectedId);
    });
    if (succeeded) {
      setTestsGeneratedFor((prev) => new Set(prev).add(functionalRequirementId));
      setRevealGroup({ key: functionalRequirementId, request: { stamp: nextStamp(), open: true } });
    }
  };

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

  // §24 — a review decision, with the reviewer's comment (the reason, for a rejection) recorded on it.
  // Resolves to whether the decision was recorded, so the card keeps the comment when it was not.
  // S1 — made on the version the card showed; a 409 (changed since) is reported like any other refusal.
  const decideArtifact = async (id: string, status: number, comment: string | undefined, version: number) => {
    let recorded = false;
    await runAction(`${status === 3 ? 'approve' : 'reject'}-${id}`, async () => {
      await artifactsApi.updateStatus(id, decisionRequest(status, comment, version), token);
      recorded = true;
      if (selectedId) await loadDerived(selectedId);
    });
    return recorded;
  };

  const onApprove = (id: string, comment: string | undefined, version: number) => decideArtifact(id, 3, comment, version); // 3 = Approved
  const onReject = (id: string, comment: string | undefined, version: number) => decideArtifact(id, 4, comment, version); // 4 = Rejected

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

  // P0-4 — re-read the coverage whenever the source's FRs/NFRs change (generated, edited, approved or
  // rejected). Only for users who may read raw sources; the server refuses anyone else.
  const coverageKey = [...functionalRequirements, ...nonFunctionalRequirements]
    .map((a) => `${a.id}:${a.currentVersion}:${a.status}`)
    .join(',');
  useEffect(() => {
    if (!showRawInput || !selectedId || loadedFor !== selectedId) return;
    let cancelled = false;
    requirementSourcesApi
      .sourceIdCoverage(selectedId, token)
      .then((value) => !cancelled && setCoverage({ sourceId: selectedId, value }))
      .catch(() => !cancelled && setCoverage({ sourceId: selectedId, value: null }));
    return () => {
      cancelled = true;
    };
  }, [showRawInput, selectedId, loadedFor, coverageKey, token]);
  const coverageForSelected = coverage.sourceId === selectedId ? coverageNotice(coverage.value) : null;
  const analysisCompleteness = analysisCompletenessNotice(analysis);
  const generationCompleteness = generationCompletenessNotice(generation);

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
    // Lets page-level summaries (e.g. Testing's QA results) reload after an edit or a recorded result.
    onChange?.();
  };

  // Presentation-only groupings, built from payload fields and the existing traceability links.
  const frGroups = groupByValue(functionalRequirements, byField('actor'), 'No actor specified');
  const nfrGroups = groupByValue(nonFunctionalRequirements, byField('category'), 'Uncategorized');
  const ruleGroups = groupByRequirement(businessRules, functionalRequirements, matrix, 'businessRules');
  const storyGroups = groupByRequirement(userStories, functionalRequirements, matrix, 'userStories');
  const criteriaGroups = groupByRequirement(acceptanceCriteria, functionalRequirements, matrix, 'acceptanceCriteria');
  const apiGroups = groupByValue(apiSpecifications, apiResourceOf, 'Other routes');
  const taskGroups =
    groupByRequirement(implementationTasks, functionalRequirements, matrix, 'implementationTasks') ??
    groupByValue(implementationTasks, byField('taskType'), 'Other');
  const taskGroupedByType = taskGroups !== null && !taskGroups.some((g) => g.requirementId);
  // Testing lists every requirement (for QA, including those without tests yet) so generation happens in place.
  const testGroups = groupByRequirement(testCases, functionalRequirements, matrix, 'testCases', { includeEmpty: canManageTests });
  const coveredRequirements = testGroups ? testGroups.filter((g) => g.requirementId && g.items.length > 0).length : 0;

  const reveal = (id: string) => (revealSection?.id === id ? revealSection.request : null);
  const jumpTo = (id: string) => {
    setRevealSection({ id, request: { stamp: nextStamp(), open: true } });
    const reduceMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
    requestAnimationFrame(() => document.getElementById(id)?.scrollIntoView({ behavior: reduceMotion ? 'auto' : 'smooth', block: 'start' }));
  };
  const overview = (items: OverviewItem[]) => <GeneratedOverview items={items.filter((i) => i.count > 0)} onJump={jumpTo} />;

  // Act on a guidance deep link once, after the requested source's artifacts have loaded: open the
  // artifact's section, group and card (the card scrolls itself into view and is highlighted), or
  // scroll to the requested section. A target not in this list (e.g. not visible to this user) is ignored.
  useEffect(() => {
    if (!requestedFocus && !requestedSection) return;
    if (!selectedId || loadedFor !== selectedId) return;
    if (requestedSource && requestedSource !== selectedId && sources.some((s) => s.id === requestedSource)) return;
    const link = `${selectedId}|${requestedFocus ?? ''}|${requestedSection ?? ''}`;
    if (handledLink.current === link) return;
    handledLink.current = link;

    const target = requestedFocus ? artifacts.find((a) => a.id === requestedFocus) : undefined;
    if (target) {
      const request = { stamp: nextStamp(), open: true };
      setFocus({ id: target.id, request });
      const section = sectionForArtifactType(target.artifactType);
      if (section) setRevealSection({ id: section, request });
      return;
    }

    if (requestedSection) {
      // Some anchors (conflicts, quality) render in panels that load on their own; retry briefly.
      const reduceMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
      const timers = [0, 400, 1200].map((delay) =>
        window.setTimeout(() => {
          const el = document.getElementById(requestedSection);
          if (el && !el.dataset.guidanceScrolled) {
            el.dataset.guidanceScrolled = '1';
            setRevealSection({ id: requestedSection, request: { stamp: nextStamp(), open: true } });
            el.scrollIntoView({ behavior: reduceMotion ? 'auto' : 'smooth', block: 'start' });
          }
        }, delay),
      );
      return () => timers.forEach((t) => window.clearTimeout(t));
    }
  }, [requestedFocus, requestedSection, requestedSource, selectedId, loadedFor, artifacts, sources]);

  const reviewProps = { canReview, canEdit: canWrite, token, onApprove, onReject, onChangeStatus, onSaved: onArtifactSaved, focus };
  // `done` — how many items this step has already produced for the source; shown as a completed state.
  // The button stays usable: running it again calls the generator again, exactly as before.
  const generateButton = (key: string, label: string, busyLabel: string, run: () => unknown, disabled: boolean, primary = false, hint?: string, done = 0) => {
    const running = busy === key;
    const isDone = done > 0 && !running;
    return (
      <button
        type="button"
        className={`btn ${primary && !isDone ? 'btn-primary' : 'btn-secondary'} btn-sm${isDone ? ' btn-done' : ''}`}
        disabled={running || disabled}
        aria-busy={running || undefined}
        onClick={run}
        title={hint ?? (isDone ? `${done} already generated for this source. Running it again calls the AI generator again.` : undefined)}
      >
        {running ? <span className="btn-spinner" aria-hidden="true" /> : isDone ? <IconCheck width={14} height={14} /> : primary && <IconSparkles width={14} height={14} />}
        {running ? busyLabel : label}
        {isDone && <span className="btn-count">{done}</span>}
      </button>
    );
  };

  // Test-case generation for one requirement: Generate → Generating… → ✓ Generated (+ Generate more, which adds another set).
  const testGenerateActions = (frId: string, generated: boolean) => {
    const fr = functionalRequirements.find((a) => a.id === frId);
    if (fr && !canGenerateDownstream(fr.status)) return null;
    const key = `generate-tc-${frId}`;
    if (busy === key) {
      return (
        <button type="button" className="btn btn-secondary btn-sm" disabled aria-busy="true">
          <span className="btn-spinner" aria-hidden="true" />
          Generating…
        </button>
      );
    }
    if (generated) {
      return (
        <>
          <span className="gen-done" role="status">
            <IconCheck width={13} height={13} /> Test cases generated
          </span>
          <button
            type="button"
            className="btn btn-ghost btn-sm"
            onClick={() => onGenerateTestCases(frId)}
            title="Generates an additional set of AI test cases for this requirement. Existing test cases are kept."
          >
            Generate more
          </button>
        </>
      );
    }
    return (
      <button type="button" className="btn btn-secondary btn-sm" onClick={() => onGenerateTestCases(frId)}>
        <IconSparkles width={14} height={14} />
        Generate test cases
      </button>
    );
  };

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
                ? 'Paste notes or upload a document above. Every later lifecycle stage — Analysis, Design and beyond — builds on this raw input.'
                : 'A business analyst needs to add a requirement source before anything appears here.'
              : 'Start by capturing the business needs for this project. This work area uses the requirement sources added during Requirements Gathering.'
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
              title={showRawInput ? s.rawText : undefined}
            >
              {s.originalFileName ? <IconUpload width={13} height={13} /> : <IconDocument width={13} height={13} />}
              {showRawInput ? (
                <span>
                  {(s.originalFileName ?? s.rawText).slice(0, 40)}
                  {(s.originalFileName ?? s.rawText).length > 40 ? '…' : ''}
                </span>
              ) : (
                <span>Source {sources.length - sources.indexOf(s)}</span>
              )}
            </button>
          ))}
        </div>
      )}

      {selectedSource && (
        <div className="req-detail">
          {isGathering && showRawInput && (
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
                {generateButton('analyze', 'Run AI analysis', 'Analyzing…', onAnalyze, false, true, planButtonHint(plansForSelected?.analyze ?? null))}
                <AiPlanSummary
                  plan={plansForSelected?.analyze ?? null}
                  loading={plansForSelected?.loading ?? true}
                  failed={plansForSelected?.failed ?? false}
                />
                {selectedId && (
                  <AiRunPanel
                    sourceId={selectedId}
                    operation="analyze"
                    plan={plansForSelected?.analyze ?? null}
                    token={token}
                    canWrite={canWrite}
                    onChanged={() => setPlanStamp((n) => n + 1)}
                    onSaved={() => {
                      loadDerived(selectedId);
                      onChange?.();
                    }}
                  />
                )}
              </PipelineStep>
              <PipelineStep n={2} title="Specify">
                {generateButton('generate-requirements', 'Generate FR / NFR', 'Generating…', onGenerateRequirements, questions.length === 0, false, questions.length === 0 ? 'Run AI analysis first' : planButtonHint(plansForSelected?.generate ?? null), functionalRequirements.length + nonFunctionalRequirements.length)}
                {questions.length > 0 && (
                  <AiPlanSummary
                    plan={plansForSelected?.generate ?? null}
                    loading={plansForSelected?.loading ?? true}
                    failed={plansForSelected?.failed ?? false}
                  />
                )}
                {questions.length > 0 && selectedId && (
                  <AiRunPanel
                    sourceId={selectedId}
                    operation="generate"
                    plan={plansForSelected?.generate ?? null}
                    token={token}
                    canWrite={canWrite}
                    onChanged={() => setPlanStamp((n) => n + 1)}
                    onSaved={() => {
                      loadDerived(selectedId);
                      onChange?.();
                    }}
                  />
                )}
                {generateButton('generate-stories', 'Generate user stories', 'Generating…', onGenerateStories, needsFr, false, needsFrHint, userStories.length)}
                {generateButton('generate-rules', 'Extract business rules', 'Generating…', onGenerateBusinessRules, needsFr, false, needsFrHint, businessRules.length)}
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
                {generateButton('generate-design', 'Solution design', 'Generating…', onGenerateDesign, needsFr, true, needsFrHint, designArtifacts.length)}
                {generateButton('generate-data-entities', 'Data entities', 'Generating…', onGenerateDataEntities, needsFr, false, needsFrHint, dataEntities.length)}
                {generateButton('generate-api-specs', 'API specs', 'Generating…', onGenerateApiSpecs, needsFr, false, needsFrHint, apiSpecifications.length)}
              </PipelineStep>
            </div>
          )}

          {canWrite && view === 'development' && (
            <div className="req-pipeline">
              <PipelineStep n={1} title="Plan implementation">
                {generateButton('generate-tasks', 'Implementation tasks', 'Generating…', onGenerateTasks, needsFr, true, needsFrHint, implementationTasks.length)}
              </PipelineStep>
            </div>
          )}

          {view === 'analysis' && analysisCompleteness && <CompletenessAlert notice={analysisCompleteness} id="sec-analysis-completeness" />}
          {view === 'analysis' && analysis && <AnalysisSummaryPanel analysis={analysis} />}

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
            <ClarificationSection questions={questions} openCount={openQuestionCount} canAnswer={canWrite} onAnswer={onAnswer} />
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
              {overview([
                { id: 'sec-functional', label: 'Functional requirements', count: functionalRequirements.length },
                { id: 'sec-nonfunctional', label: 'Non-functional requirements', count: nonFunctionalRequirements.length },
                { id: 'sec-rules', label: 'Business rules', count: businessRules.length },
                { id: 'sec-stories', label: 'User stories', count: userStories.length },
                { id: 'sec-criteria', label: 'Acceptance criteria', count: acceptanceCriteria.length },
              ])}
              {generationCompleteness && <CompletenessAlert notice={generationCompleteness} id="sec-completeness" />}
              {coverageForSelected && functionalRequirements.length + nonFunctionalRequirements.length > 0 && (
                <div className={`alert alert-${coverageForSelected.tone} req-alert`} role="status" id="sec-id-coverage">
                  {coverageForSelected.tone === 'success' ? <IconCheckCircle width={16} height={16} /> : <IconAlert width={16} height={16} />}
                  <div>
                    <strong>{coverageForSelected.title}</strong>
                    {coverageForSelected.details.map((line) => (
                      <div key={line}>{line}</div>
                    ))}
                  </div>
                </div>
              )}
              {functionalRequirements.length > 0 && (
                <ArtifactSection id="sec-functional" title="Functional Requirements" noun={['requirement', 'requirements']} items={functionalRequirements}
                  groups={frGroups} groupEyebrow="Actor" reveal={reveal('sec-functional')} {...reviewProps} />
              )}
              {nonFunctionalRequirements.length > 0 && (
                <ArtifactSection id="sec-nonfunctional" title="Non-Functional Requirements" noun={['requirement', 'requirements']} items={nonFunctionalRequirements}
                  groups={nfrGroups} groupEyebrow="Category" reveal={reveal('sec-nonfunctional')} {...reviewProps} />
              )}
              {businessRules.length > 0 && (
                <ArtifactSection id="sec-rules" title="Business Rules" noun={['rule', 'rules']} items={businessRules}
                  groups={ruleGroups} reveal={reveal('sec-rules')} {...reviewProps} />
              )}
              {userStories.length > 0 && (
                <ArtifactSection id="sec-stories" title="User Stories" noun={['story', 'stories']} items={userStories}
                  groups={storyGroups} reveal={reveal('sec-stories')} {...reviewProps} onGenerateAcceptanceCriteria={onGenerateAcceptanceCriteria} />
              )}
              {acceptanceCriteria.length > 0 && (
                <ArtifactSection id="sec-criteria" title="Acceptance Criteria" noun={['criterion', 'criteria']} items={acceptanceCriteria}
                  groups={criteriaGroups} reveal={reveal('sec-criteria')} {...reviewProps} />
              )}
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
              {overview([
                { id: 'sec-architecture', label: 'Solution design', count: designArtifacts.length },
                { id: 'sec-data', label: 'Data entities', count: dataEntities.length },
                { id: 'sec-api', label: 'API specifications', count: apiSpecifications.length },
              ])}
              {designArtifacts.length > 0 && (
                <ArtifactSection id="sec-architecture" title="Solution Design" noun={['design', 'designs']} items={designArtifacts}
                  reveal={reveal('sec-architecture')} {...reviewProps} />
              )}
              {dataEntities.length > 0 && (
                <ArtifactSection id="sec-data" title="Data Entities" noun={['entity', 'entities']} items={dataEntities}
                  reveal={reveal('sec-data')} {...reviewProps} />
              )}
              {apiSpecifications.length > 0 && (
                <ArtifactSection id="sec-api" title="API Specifications" noun={['endpoint', 'endpoints']} items={apiSpecifications}
                  groups={apiGroups} groupEyebrow="Resource" reveal={reveal('sec-api')} {...reviewProps} />
              )}
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
              {implementationTasks.length > 0 && (
                <ArtifactSection id="sec-tasks" title="Implementation Tasks" noun={['task', 'tasks']} items={implementationTasks}
                  groups={taskGroups} groupEyebrow={taskGroupedByType ? 'Type' : undefined} reveal={reveal('sec-tasks')} {...reviewProps} />
              )}
            </>
          )}

          {view === 'testing' && (
            <>
              {testGroups ? (
                // Grouped by requirement: each requirement shows its test cases and generates them in place.
                (testCases.length > 0 || (canManageTests && functionalRequirements.length > 0)) && (
                  <ArtifactSection
                    id="sec-tests"
                    title="Test Cases"
                    noun={['test case', 'test cases']}
                    items={testCases}
                    groups={testGroups}
                    emptyGroupLabel="Not generated yet"
                    summary={`${coveredRequirements} of ${functionalRequirements.length} requirements covered`}
                    groupActions={canManageTests ? (g) => g.requirementId && testGenerateActions(g.requirementId, g.items.length > 0) : undefined}
                    groupReveal={(g) => (revealGroup && revealGroup.key === g.key ? revealGroup.request : null)}
                    reveal={reveal('sec-tests')}
                    {...reviewProps}
                    canEdit={canManageTests}
                  />
                )
              ) : (
                <>
                  {canManageTests && functionalRequirements.length > 0 && (
                    <section className="req-section">
                      <div className="req-section-head">
                        <h3>Generate test cases per requirement</h3>
                        <span className="req-count">{functionalRequirements.length}</span>
                      </div>
                      <ul className="tc-source-list">
                        {functionalRequirements.map((fr) => (
                          <li key={fr.id}>
                            <span className="code">{fr.code}</span>
                            <span className="tc-source-title">{fr.title}</span>
                            <span className="tc-source-actions">{testGenerateActions(fr.id, testsGeneratedFor.has(fr.id))}</span>
                          </li>
                        ))}
                      </ul>
                    </section>
                  )}
                  {testCases.length > 0 && (
                    <ArtifactSection id="sec-tests" title="Test Cases" noun={['test case', 'test cases']} items={testCases} reveal={reveal('sec-tests')} {...reviewProps} canEdit={canManageTests} />
                  )}
                </>
              )}
              {phaseArtifactCount === 0 && (needsFr || !canManageTests) && (
                <EmptyState
                  compact
                  icon={<IconInbox width={18} height={18} />}
                  title="No test cases yet"
                  description={needsFr ? 'Generate functional requirements first — test cases are derived from them.' : 'Test cases have not been generated from these requirements yet.'}
                />
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}

// P0-7 — the completeness notice of the last Analyze/Generate result (lib/completeness).
function CompletenessAlert({ notice, id }: { notice: CompletenessNotice; id: string }) {
  return (
    <div className={`alert alert-${notice.tone} req-alert`} role="status" id={id}>
      {notice.tone === 'success' ? <IconCheckCircle width={16} height={16} /> : <IconAlert width={16} height={16} />}
      <div>
        <strong>{notice.title}</strong>
        {notice.details.map((line) => (
          <div key={line}>{line}</div>
        ))}
      </div>
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

// Chips shown per summary row, and characters of notes shown, before "Show more".
const VISIBLE_CHIPS = 8;
const NOTES_PREVIEW_CHARS = 280;

function AnalysisSummaryPanel({ analysis }: { analysis: AnalyzeRequirementResult }) {
  const [open, setOpen] = useState(true);
  const [notesOpen, setNotesOpen] = useState(true);
  const bodyId = 'req-analysis-summary-body';
  const notesId = 'req-analysis-notes-body';
  return (
    <div className="req-ai-panel">
      <div className="req-ai-panel-head">
        <span className="ai-badge">
          <IconSparkles width={12} height={12} /> AI Generated — Human Review Required
        </span>
        <h3>
          <button type="button" className="gen-toggle" aria-expanded={open} aria-controls={bodyId} onClick={() => setOpen((v) => !v)}>
            <IconChevronDown width={16} height={16} className="gen-chevron" />
            <span className="gen-title">Analysis summary</span>
          </button>
        </h3>
      </div>
      <div id={bodyId} hidden={!open}>
        <dl className="req-summary">
          <SummaryRow label="Actors" items={analysis.actors} />
          <SummaryRow label="Capabilities" items={analysis.capabilities} />
          <SummaryRow label="Data elements" items={analysis.dataElements} />
        </dl>
        {analysis.notes.length > 0 && (
          <div className="req-notes">
            <button
              type="button"
              className="gen-toggle req-notes-toggle"
              aria-expanded={notesOpen}
              aria-controls={notesId}
              onClick={() => setNotesOpen((v) => !v)}
            >
              <IconChevronDown width={14} height={14} className="gen-chevron" />
              <span className="detail-label">Notes</span>
            </button>
            <div id={notesId} hidden={!notesOpen}>
              <ExpandableText text={analysis.notes.join(' ')} />
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

function ExpandableText({ text }: { text: string }) {
  const [showAll, setShowAll] = useState(false);
  const long = text.length > NOTES_PREVIEW_CHARS;
  const shown = long && !showAll ? `${text.slice(0, NOTES_PREVIEW_CHARS).trimEnd()}…` : text;
  return (
    <p className="req-notes-text">
      {shown}
      {long && (
        <>
          {' '}
          <button type="button" className="req-show-more" aria-expanded={showAll} onClick={() => setShowAll((v) => !v)}>
            {showAll ? 'Show less' : 'Show more'}
          </button>
        </>
      )}
    </p>
  );
}

function SummaryRow({ label, items }: { label: string; items: string[] }) {
  const [showAll, setShowAll] = useState(false);
  const hidden = items.length - VISIBLE_CHIPS;
  const shown = hidden > 0 && !showAll ? items.slice(0, VISIBLE_CHIPS) : items;
  return (
    <div className="req-summary-row">
      <dt>{label}</dt>
      <dd>
        {items.length === 0 ? (
          <span className="req-none">—</span>
        ) : (
          shown.map((item, i) => (
            <span key={i} className="req-chip">
              {item}
            </span>
          ))
        )}
        {hidden > 0 && (
          <button type="button" className="req-show-more" aria-expanded={showAll} onClick={() => setShowAll((v) => !v)}>
            {showAll ? 'Show less' : `+${hidden} more`}
          </button>
        )}
      </dd>
    </div>
  );
}

function ArtifactSection({
  id,
  title,
  noun,
  items,
  groups,
  groupEyebrow,
  emptyGroupLabel,
  summary,
  reveal,
  groupActions,
  groupReveal,
  canReview,
  canEdit,
  token,
  onApprove,
  onReject,
  onChangeStatus,
  onSaved,
  onGenerateAcceptanceCriteria,
  onGenerateTestCases,
  focus,
}: {
  id?: string;
  title: string;
  /** Singular and plural item name, for group counts ("5 test cases"). */
  noun: [string, string];
  items: ArtifactSummary[];
  /** Null or omitted when the data supports no meaningful grouping: the items render as one compact list. */
  groups?: ArtifactGroup[] | null;
  groupEyebrow?: string;
  emptyGroupLabel?: string;
  summary?: string;
  reveal?: DisclosureRequest | null;
  groupActions?: (group: ArtifactGroup) => ReactNode;
  groupReveal?: (group: ArtifactGroup) => DisclosureRequest | null;
  canReview: boolean;
  canEdit: boolean;
  token: string | null;
  onApprove: (id: string, comment: string | undefined, version: number) => Promise<boolean>;
  onReject: (id: string, comment: string | undefined, version: number) => Promise<boolean>;
  onChangeStatus: (id: string, status: number) => Promise<unknown>;
  onSaved: () => void;
  onGenerateAcceptanceCriteria?: (id: string) => Promise<unknown>;
  onGenerateTestCases?: (id: string) => Promise<unknown>;
  /** A guidance deep link's target: its group and card open, and the card is highlighted. */
  focus?: { id: string; request: DisclosureRequest } | null;
}) {
  // A lone item is shown in full; in a collection, cards start compact.
  const collapsible = items.length > 1;
  const changeStatus = async (artifactId: string, status: number) => {
    await onChangeStatus(artifactId, status);
  };
  const generateCriteria = onGenerateAcceptanceCriteria && (async (storyId: string) => void (await onGenerateAcceptanceCriteria(storyId)));
  const generateTests = onGenerateTestCases && (async (frId: string) => void (await onGenerateTestCases(frId)));
  const list = (members: ArtifactSummary[]) => (
    <ul className="artifact-list">
      {members.map((a) => (
        <ArtifactCard
          key={a.id}
          artifact={a}
          canReview={canReview}
          canEdit={canEdit}
          token={token}
          onApprove={onApprove}
          onReject={onReject}
          onChangeStatus={changeStatus}
          onSaved={onSaved}
          onGenerateAcceptanceCriteria={generateCriteria}
          onGenerateTestCases={generateTests}
          collapsible={collapsible}
          reveal={focus?.id === a.id ? focus.request : null}
          focused={focus?.id === a.id}
        />
      ))}
    </ul>
  );
  // Small collections open every group; larger ones open only the first group with content.
  const firstFilled = groups?.findIndex((g) => g.items.length > 0) ?? -1;

  return (
    <CollectionSection
      id={id}
      title={title}
      count={items.length}
      summary={summary ?? statusSummary(items)}
      bulkControls={collapsible || (groups?.length ?? 0) > 1}
      reveal={reveal}
    >
      {groups ? (
        <CollectionGroups>
          {groups.map((g, i) => (
            <CollectionGroup
              key={g.key}
              code={g.code}
              eyebrow={g.requirementId ? undefined : groupEyebrow}
              label={g.label}
              count={g.items.length}
              countLabel={plural(g.items.length, noun[0], noun[1])}
              emptyLabel={emptyGroupLabel}
              summary={statusSummary(g.items)}
              actions={groupActions?.(g)}
              defaultOpen={items.length <= 6 || i === firstFilled}
              reveal={focus && g.items.some((a) => a.id === focus.id) ? focus.request : groupReveal?.(g)}
            >
              {list(g.items)}
            </CollectionGroup>
          ))}
        </CollectionGroups>
      ) : (
        list(items)
      )}
    </CollectionSection>
  );
}

const CLARIFICATION_ORDER: { status: ClarificationQuestionData['clarificationStatus']; label: string }[] = [
  { status: 'Open', label: 'Open' },
  { status: 'Answered', label: 'Answered' },
  { status: 'Resolved', label: 'Resolved' },
  { status: 'NotApplicable', label: 'Not applicable' },
];

/** Clarification questions grouped by their existing status, open questions first and expanded. */
function ClarificationSection({
  questions,
  openCount,
  canAnswer,
  onAnswer,
}: {
  questions: ArtifactSummary[];
  openCount: number;
  canAnswer: boolean;
  onAnswer: (artifactId: string, answer: string, notApplicable: boolean) => Promise<unknown>;
}) {
  const statusOf = (q: ArtifactSummary) => (q.data as unknown as ClarificationQuestionData).clarificationStatus;
  const groups = CLARIFICATION_ORDER.map((o) => ({ ...o, items: questions.filter((q) => statusOf(q) === o.status) })).filter((g) => g.items.length > 0);
  const answer = async (artifactId: string, text: string, notApplicable: boolean) => {
    await onAnswer(artifactId, text, notApplicable);
  };
  const list = (items: ArtifactSummary[]) => (
    <ul className="cq-list">
      {items.map((q) => (
        <ClarificationQuestionCard key={q.id} artifact={q} canAnswer={canAnswer} onAnswer={answer} />
      ))}
    </ul>
  );

  return (
    <CollectionSection
      id="sec-clarifications"
      title="Clarification questions"
      count={questions.length}
      summary={openCount > 0 ? `${openCount} open` : 'All addressed'}
      bulkControls={groups.length > 1}
    >
      {groups.length > 1 ? (
        <CollectionGroups>
          {groups.map((g, i) => (
            <CollectionGroup
              key={g.status}
              label={g.label}
              count={g.items.length}
              countLabel={plural(g.items.length, 'question', 'questions')}
              defaultOpen={g.status === 'Open' || (openCount === 0 && i === 0)}
            >
              {list(g.items)}
            </CollectionGroup>
          ))}
        </CollectionGroups>
      ) : (
        list(questions)
      )}
    </CollectionSection>
  );
}
