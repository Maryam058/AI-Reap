import { Link, useSearchParams } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { useProject } from '../context/ProjectContext';
import { useProjectInsights } from '../hooks/useProjectInsights';
import { ARTIFACT_TYPE_VALUES } from '../api/types';
import { SDLC_PHASES, phasePath } from '../lib/sdlc';
import { PageHeader } from '../components/ui/PageHeader';
import { Tabs } from '../components/ui/Tabs';
import { PhasePage, PlannedCard } from '../components/PhasePage';
import { RequirementWorkspace, type WorkspaceView } from '../components/RequirementWorkspace';
import { StakeholdersPanel } from '../components/StakeholdersPanel';
import { ProjectEditor, ProjectSummary } from '../components/ProjectForm';
import { ConflictsPanel } from '../components/ConflictsPanel';
import { TraceabilityMatrix } from '../components/TraceabilityMatrix';
import { CopilotPanel } from '../components/CopilotPanel';
import { AgentPipelinePanel } from '../components/AgentPipelinePanel';
import { DocumentsPanel } from '../components/DocumentsPanel';
import { AuditTrailPanel } from '../components/AuditTrailPanel';
import { useProjectEditing } from './ProjectFormPages';
import { IconBarChart, IconCheckCircle, IconCode, IconLayers, IconLink, IconShieldCheck, IconSparkles } from '../components/icons';

const phase = (key: string) => SDLC_PHASES.find((p) => p.key === key)!;

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

type GatheringTab = 'context' | 'stakeholders' | 'sources';

export function GatheringPage() {
  const { hasRole } = useAuth();
  const canManage = hasRole('Administrator') || hasRole('BusinessAnalyst');
  const [params, setParams] = useSearchParams();
  const requested = params.get('tab');
  const tab: GatheringTab = requested === 'stakeholders' || requested === 'sources' ? requested : 'context';
  const { project } = useProject();
  const editing = useProjectEditing();

  return (
    <PhasePage phaseKey="gathering" title={phase('gathering').navLabel} description="Capture the business context, stakeholders, goals and raw requirement input that every later phase builds on.">
      <Tabs
        label="Requirements gathering sections"
        active={tab}
        onChange={(k) => setParams(k === 'context' ? {} : { tab: k }, { replace: true })}
        items={[
          { key: 'context', label: 'Project context' },
          { key: 'stakeholders', label: 'Stakeholders' },
          { key: 'sources', label: 'Requirement sources' },
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
        {tab === 'stakeholders' && <StakeholdersPanel projectId={project.id} />}
        {tab === 'sources' && <Workspace view="gathering" />}
      </div>
    </PhasePage>
  );
}

/* ------------------------------------------------------------------ 2. Analysis */

export function AnalysisPage() {
  const { project, contentVersion } = useProject();
  const { dashboard } = useProjectInsights(project.id, contentVersion);
  return (
    <PhasePage phaseKey="analysis" title={phase('analysis').navLabel} description="Analyze raw input into structured functional and non-functional requirements, user stories and business rules.">
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
  const { project, contentVersion } = useProject();
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
    <PhasePage phaseKey="validation" title={phase('validation').navLabel} description="Validate requirements for ambiguity, conflicts and completeness before design begins.">
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
      <ConflictsPanel projectId={project.id} />
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
    <PhasePage phaseKey="design" title={phase('design').navLabel} description="Derive the architecture, data model and interfaces from validated requirements.">
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
    <PhasePage phaseKey="development" title={phase('development').navLabel} description="Break the design into implementation tasks and keep them linked to requirements.">
      <Workspace view="development" />
      <PlannedCard
        icon={<IconCode width={18} height={18} />}
        title="Task board"
        description="Track implementation progress instead of only listing generated tasks."
        items={['Status and assignee per task', 'Priority', 'Linked requirements on every task']}
      />
    </PhasePage>
  );
}

export function TestingPage() {
  const { project, contentVersion } = useProject();
  const { artifacts, matrix } = useProjectInsights(project.id, contentVersion);
  const testCount = artifacts ? artifacts.filter((a) => a.artifactType === ARTIFACT_TYPE_VALUES.TestCase).length : null;
  const covered = matrix && matrix.length > 0 ? matrix.filter((r) => r.testCases.length > 0).length : null;
  return (
    <PhasePage phaseKey="testing" title={phase('testing').navLabel} description="Derive test cases from functional requirements and see how much of the specification they cover.">
      {(testCount !== null || covered !== null) && (
        <div className="stat-grid phase-stats">
          {testCount !== null && <Stat label="Test cases" value={testCount} />}
          {covered !== null && matrix && <Stat label="Requirements with tests" value={`${covered} / ${matrix.length}`} hint="Functional requirements with at least one linked test case" />}
        </div>
      )}
      <Workspace view="testing" />
      <PlannedCard
        icon={<IconShieldCheck width={18} height={18} />}
        title="Test execution results"
        description="Recording runs and outcomes against test cases."
        items={['Passed / failed / blocked status', 'Execution history', 'Defect links']}
      />
    </PhasePage>
  );
}

/* ------------------------------------------------------------------ 7. Traceability */

export function TraceabilityPage() {
  const { project } = useProject();
  return (
    <PhasePage phaseKey="traceability" title={phase('traceability').navLabel} description="Follow every requirement through design, development and testing to prove nothing is missed.">
      <TraceabilityMatrix projectId={project.id} />
    </PhasePage>
  );
}

/* ------------------------------------------------------------------ AI Copilot */

type CopilotTab = 'ask' | 'pipeline' | 'documents' | 'audit';

export function CopilotPage() {
  const { project, bumpContent, contentVersion } = useProject();
  const [params, setParams] = useSearchParams();
  const requested = params.get('tab');
  const tab: CopilotTab = requested === 'pipeline' || requested === 'documents' || requested === 'audit' ? requested : 'ask';
  const fromPhase = SDLC_PHASES.find((p) => p.key === params.get('phase'));

  return (
    <>
      <PageHeader
        title="AI Copilot"
        subtitle="Ask about gaps, coverage and recent changes — answers are grounded in this project's requirements and uploaded documents."
        badge={<span className="copilot-chip">Project: {project.name}</span>}
        actions={
          fromPhase && (
            <Link to={phasePath(project.id, fromPhase.segment)} className="btn btn-secondary">
              Back to {fromPhase.label}
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
          { key: 'documents', label: 'Project documents', icon: <IconLink width={15} height={15} /> },
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
    </>
  );
}
