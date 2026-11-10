import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { useProject } from '../context/ProjectContext';
import { useProjectInsights } from '../hooks/useProjectInsights';
import { ARTIFACT_TYPE_VALUES } from '../api/types';
import { requirementSourcesApi } from '../api/requirementSources';
import { phasePath, WORK_AREAS } from '../lib/workAreas';
import { readsRawSources } from '../lib/access';
import { Tabs } from '../components/ui/Tabs';
import { PhasePage, PlannedCard } from '../components/PhasePage';
import { RequirementWorkspace, type WorkspaceView } from '../components/RequirementWorkspace';
import { StakeholdersPanel } from '../components/StakeholdersPanel';
import { MembersPanel } from '../components/MembersPanel';
import { overallCompletion, ProjectEditor, ProjectSummary } from '../components/ProjectForm';
import { StageHeader, StageNextStep } from '../components/stage/StageHeader';
import { ConflictsPanel } from '../components/ConflictsPanel';
import { TraceabilityMatrix } from '../components/TraceabilityMatrix';
import { TestQaSummary } from '../components/TestQaSummary';
import { CopilotPanel } from '../components/CopilotPanel';
import { AgentPipelinePanel } from '../components/AgentPipelinePanel';
import { DocumentsPanel } from '../components/DocumentsPanel';
import { AuditTrailPanel } from '../components/AuditTrailPanel';
import { useProjectEditing } from './ProjectFormPages';
import { IconArrowLeft, IconBarChart, IconCheck, IconCheckCircle, IconDocument, IconLayers, IconLink, IconShieldCheck, IconSparkles, IconTarget, IconUpload, IconUsers } from '../components/icons';

function Stat({ label, value, hint }: { label: string; value: number | string; hint?: string }) {
  return (
    <div className="stat-tile" title={hint}>
      <div className="stat-value">{value}</div>
      <div className="stat-label">{label}</div>
    </div>
  );
}

/** Workspace slice for one phase, wired to the shell so dashboards refresh when content changes. */
function Workspace({ view }: { view: WorkspaceView }) {
  const { project, bumpContent } = useProject();
  return <RequirementWorkspace projectId={project.id} view={view} onChange={bumpContent} />;
}

/* ------------------------------------------------------------------ 1. Gathering */

type GatheringTab = 'context' | 'stakeholders' | 'members' | 'sources';

export function GatheringPage() {
  const { hasRole } = useAuth();
  const canManage = hasRole('Administrator') || hasRole('BusinessAnalyst');
  const [params, setParams] = useSearchParams();
  const requested = params.get('tab');
  const tab: GatheringTab =
    requested === 'stakeholders' || requested === 'members' || requested === 'sources' ? requested : 'context';
  const { project, bumpContent, contentVersion } = useProject();
  const editing = useProjectEditing();
  const select = (k: GatheringTab) => setParams(k === 'context' ? {} : { tab: k }, { replace: true });

  return (
    <PhasePage phaseKey="gathering">
      <GatheringSteps active={tab} onSelect={select} contextDone={overallCompletion(editing.saved)} version={contentVersion} />
      <Tabs
        label="Requirements gathering sections"
        active={tab}
        onChange={(k) => select(k as GatheringTab)}
        items={[
          { key: 'context', label: 'Project context', icon: <IconTarget width={15} height={15} /> },
          { key: 'stakeholders', label: 'Stakeholders', icon: <IconUsers width={15} height={15} /> },
          { key: 'members', label: 'Members', icon: <IconUsers width={15} height={15} /> },
          { key: 'sources', label: 'Requirement sources (SRS)', icon: <IconUpload width={15} height={15} /> },
        ]}
      />
      <div role="tabpanel" id={`panel-${tab}`} aria-labelledby={`tab-${tab}`} className="tab-panel">
        {tab === 'context' &&
          (canManage ? (
            <ProjectEditor
              values={editing.values}
              saved={editing.saved}
              onChange={editing.change}
              onSave={editing.save}
              busy={editing.busy}
              error={editing.error}
              saveLabel="Save Project Details"
              onCancel={editing.reset}
            />
          ) : (
            <section className="panel">
              <header className="panel-head">
                <div>
                  <h2>Project context</h2>
                  <p>You have read access. Ask an administrator or business analyst to change these details.</p>
                </div>
              </header>
              <ProjectSummary values={editing.saved} />
            </section>
          ))}
        {tab === 'stakeholders' && <StakeholdersPanel projectId={project.id} onChange={bumpContent} />}
        {tab === 'members' && <MembersPanel projectId={project.id} />}
        {tab === 'sources' && <Workspace view="gathering" />}
      </div>
    </PhasePage>
  );
}


/**
 * The order of work in Gathering, as numbered steps that open the matching tab. Each step shows
 * only real figures: filled project details, recorded stakeholders, and requirement sources added.
 */
function GatheringSteps({
  active,
  onSelect,
  contextDone,
  version,
}: {
  active: GatheringTab;
  onSelect: (tab: GatheringTab) => void;
  contextDone: { done: number; total: number };
  version: number;
}) {
  const { token } = useAuth();
  const { project } = useProject();
  const { stakeholders } = useProjectInsights(project.id, version);
  const [sources, setSources] = useState<number | null>(null);

  useEffect(() => {
    let cancelled = false;
    requirementSourcesApi
      .listForProject(project.id, token)
      .then((list) => !cancelled && setSources(list.length))
      .catch(() => !cancelled && setSources(null));
    return () => {
      cancelled = true;
    };
  }, [project.id, token, version]);

  const steps: { key: GatheringTab; title: string; text: string; meta: string | null; done: boolean; icon: ReactNode }[] = [
    {
      key: 'context',
      title: 'Describe the project',
      text: 'Problem, goals, scope and users.',
      meta: `${contextDone.done} of ${contextDone.total} details filled`,
      done: contextDone.done === contextDone.total,
      icon: <IconTarget width={15} height={15} />,
    },
    {
      key: 'stakeholders',
      title: 'Record stakeholders',
      text: 'Who is affected and who decides.',
      meta: stakeholders ? (stakeholders.length === 0 ? 'None recorded yet' : `${stakeholders.length} recorded`) : null,
      done: !!stakeholders && stakeholders.length > 0,
      icon: <IconUsers width={15} height={15} />,
    },
    {
      key: 'members',
      title: 'Add team members',
      text: 'Give colleagues access with a role.',
      meta: 'Members can sign in and work here',
      done: false,
      icon: <IconUsers width={15} height={15} />,
    },
    {
      key: 'sources',
      title: 'Upload your SRS',
      text: 'Upload a document or paste notes.',
      meta: sources === null ? null : sources === 0 ? 'Nothing added yet' : `${sources} ${sources === 1 ? 'source' : 'sources'} added`,
      done: (sources ?? 0) > 0,
      icon: <IconDocument width={15} height={15} />,
    },
  ];

  return (
    <ol className="stage-steps" aria-label="Steps in this stage">
      {steps.map((st, i) => (
        <li key={st.key} className={`${st.key === active ? 'active' : ''}${st.done ? ' done' : ''}`}>
          <button type="button" onClick={() => onSelect(st.key)} aria-pressed={st.key === active}>
            <span className="stage-steps-num" aria-hidden="true">
              {st.done ? <IconCheck width={13} height={13} strokeWidth={3} /> : i + 1}
            </span>
            <span className="stage-steps-text">
              <strong>
                {st.icon} {st.title}
              </strong>
              <span>{st.text}</span>
              {st.meta && <small>{st.meta}</small>}
            </span>
          </button>
        </li>
      ))}
    </ol>
  );
}

/* ------------------------------------------------------------------ 2. Analysis */

export function AnalysisPage() {
  const { project, contentVersion } = useProject();
  const { dashboard } = useProjectInsights(project.id, contentVersion);
  return (
    <PhasePage phaseKey="analysis">
      {dashboard && (
        <div className="stat-grid phase-stats">
          <Stat label="Functional requirements" value={dashboard.functionalRequirementCount} />
          <Stat label="Non-functional requirements" value={dashboard.nonFunctionalRequirementCount} />
          <Stat label="Business rules" value={dashboard.businessRuleCount} />
          <Stat label="User stories" value={dashboard.userStoryCount} />
          <Stat label="Acceptance criteria" value={dashboard.acceptanceCriterionCount} />
        </div>
      )}
      <Workspace view="analysis" />
    </PhasePage>
  );
}

/* ------------------------------------------------------------------ 3. Validation */

export function ValidationPage() {
  const { project, contentVersion, bumpContent } = useProject();
  const { dashboard, artifacts } = useProjectInsights(project.id, contentVersion);
  const requirements = artifacts?.filter((a) => a.artifactType === ARTIFACT_TYPE_VALUES.FunctionalRequirement || a.artifactType === ARTIFACT_TYPE_VALUES.NonFunctionalRequirement) ?? null;

  // Readiness is derived only from real counts.
  const readiness = (() => {
    if (!dashboard || !requirements) return null;
    if (requirements.length === 0) return { tone: 'neutral', label: 'Not started', text: 'No requirements exist yet to validate.' };
    const issues = dashboard.openClarificationCount + dashboard.pendingReviewCount + dashboard.rejectedCount;
    return issues > 0
      ? { tone: 'warning', label: 'Needs attention', text: 'Open questions or unreviewed artifacts remain.' }
      : { tone: 'success', label: 'Ready for design', text: 'No open questions, pending reviews or rejections.' };
  })();

  return (
    <PhasePage phaseKey="validation">
      {dashboard && requirements && readiness && (
        <section className="panel readiness">
          <div className="readiness-head">
            <div>
              <span className="eyebrow">Validation readiness</span>
              <div className="readiness-status">
                <span className={`status-pill tone-${readiness.tone}`}>{readiness.label}</span>
                <span className="muted">{readiness.text}</span>
              </div>
            </div>
          </div>
          <div className="stat-grid phase-stats">
            <Stat label="Requirements checked" value={requirements.length} />
            <Stat label="Approved" value={dashboard.approvedCount} />
            <Stat label="Awaiting review" value={dashboard.pendingReviewCount} />
            <Stat label="Rejected" value={dashboard.rejectedCount} />
            <Stat label="Open clarifications" value={dashboard.openClarificationCount} />
          </div>
        </section>
      )}
      <Workspace view="validation" />
      <ConflictsPanel projectId={project.id} onChange={bumpContent} />
      <PlannedCard
        icon={<IconCheckCircle width={18} height={18} />}
        title="Completeness checks & AI suggestions"
        description="Automatic detection of missing requirement areas, with suggested fixes."
        items={['Coverage of expected requirement categories', 'Suggested wording improvements', 'Readiness scoring']}
      />
    </PhasePage>
  );
}

/* ------------------------------------------------------------------ 4-6 Design / Development / Testing */

export function DesignPage() {
  return (
    <PhasePage phaseKey="design">
      <Workspace view="design" />
      <PlannedCard
        icon={<IconLayers width={18} height={18} />}
        title="Components, integrations & technical decisions"
        description="A structured view of system components and the decisions behind them."
        items={['Component breakdown', 'External integrations', 'Architecture decision records']}
      />
    </PhasePage>
  );
}

export function DevelopmentPage() {
  return (
    <PhasePage phaseKey="development">
      <Workspace view="development" />
    </PhasePage>
  );
}

export function TestingPage() {
  const { project, contentVersion } = useProject();
  const { artifacts, matrix } = useProjectInsights(project.id, contentVersion);
  const testCases = useMemo(() => artifacts?.filter((a) => a.artifactType === ARTIFACT_TYPE_VALUES.TestCase) ?? null, [artifacts]);
  const testCount = testCases ? testCases.length : null;
  const covered = matrix && matrix.length > 0 ? matrix.filter((r) => r.testCases.length > 0).length : null;
  return (
    <PhasePage phaseKey="testing">
      {testCases && <TestQaSummary projectId={project.id} testCases={testCases} />}
      {(testCount !== null || covered !== null) && (
        <div className="stat-grid phase-stats">
          {testCount !== null && <Stat label="Test cases" value={testCount} />}
          {covered !== null && matrix && <Stat label="Requirements with tests" value={`${covered} / ${matrix.length}`} hint="Functional requirements with at least one linked test case" />}
        </div>
      )}
      <Workspace view="testing" />
    </PhasePage>
  );
}

/* ------------------------------------------------------------------ 7. Traceability */

export function TraceabilityPage() {
  const { project } = useProject();
  return (
    <PhasePage phaseKey="traceability">
      <TraceabilityMatrix projectId={project.id} />
    </PhasePage>
  );
}

/* ------------------------------------------------------------------ AI Copilot */

type CopilotTab = 'ask' | 'pipeline' | 'documents' | 'audit';

export function CopilotPage() {
  const { project, bumpContent, contentVersion } = useProject();
  const { hasRole } = useAuth();
  // Project documents are raw source material, which the API withholds from Developers (S2).
  const showDocuments = readsRawSources(hasRole);
  const [params, setParams] = useSearchParams();
  const requested = params.get('tab');
  const tab: CopilotTab =
    requested === 'pipeline' || (requested === 'documents' && showDocuments) || requested === 'audit' ? requested : 'ask';
  const fromPhase = WORK_AREAS.find((p) => p.key === params.get('phase'));

  return (
    <>
      <StageHeader
        stageKey="copilot"
        project={project}
        badge={<span className="copilot-chip">Answers are grounded in this project's requirements and documents</span>}
        actions={
          fromPhase && (
            <Link to={phasePath(project.id, fromPhase.segment)} className="btn btn-secondary">
              <IconArrowLeft width={15} height={15} /> Back to {fromPhase.label}
            </Link>
          )
        }
      />
      <Tabs
        label="AI workspace sections"
        active={tab}
        onChange={(k) => setParams(k === 'ask' ? (fromPhase ? { phase: fromPhase.key } : {}) : { tab: k }, { replace: true })}
        items={[
          { key: 'ask', label: 'Ask Copilot', icon: <IconSparkles width={15} height={15} /> },
          { key: 'pipeline', label: 'Agent pipeline', icon: <IconBarChart width={15} height={15} /> },
          ...(showDocuments ? [{ key: 'documents' as const, label: 'Project documents', icon: <IconLink width={15} height={15} /> }] : []),
          { key: 'audit', label: 'AI audit trail', icon: <IconShieldCheck width={15} height={15} /> },
        ]}
      />
      <div role="tabpanel" id={`panel-${tab}`} aria-labelledby={`tab-${tab}`} className="tab-panel">
        {tab === 'ask' && (
          <section className="panel">
            <CopilotPanel projectId={project.id} contextLabel={fromPhase ? fromPhase.navLabel : undefined} />
          </section>
        )}
        {tab === 'pipeline' && <AgentPipelinePanel projectId={project.id} refreshKey={contentVersion} onChange={bumpContent} />}
        {tab === 'documents' && <DocumentsPanel projectId={project.id} />}
        {tab === 'audit' && <AuditTrailPanel projectId={project.id} />}
      </div>
      <StageNextStep stageKey="copilot" project={project} />
    </>
  );
}
