import { Link } from 'react-router-dom';
import { ARTIFACT_TYPE_VALUES, type ArtifactSummary, type ProjectDashboard, type TraceabilityRow, type Stakeholder } from '../../api/types';
import { activeStages, STAGE_COUNT, STAGES, stageGaps, stageStatus, type StageKey, type StageSignals } from '../../lib/stages';
import { phasePath } from '../../lib/workAreas';
import { IconArrowRight } from '../icons';
import { StageIcon } from './StageIcon';
import { StageStatusPill } from './StageHeader';

interface JourneyData {
  dashboard: ProjectDashboard | null;
  artifacts: ArtifactSummary[] | null;
  stakeholders: Stakeholder[] | null;
  matrix: TraceabilityRow[] | null;
}

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/** One real figure per stage, from data the overview already loads; null when it is not loaded. */
function stageFigure(key: StageKey, d: JourneyData): string | null {
  const T = ARTIFACT_TYPE_VALUES;
  const count = (...types: number[]) => (d.artifacts ? d.artifacts.filter((a) => types.includes(a.artifactType)).length : null);
  switch (key) {
    case 'gathering':
      return d.stakeholders ? plural(d.stakeholders.length, 'stakeholder', 'stakeholders') + ' recorded' : null;
    case 'analysis':
      return d.dashboard
        ? `${plural(d.dashboard.functionalRequirementCount + d.dashboard.nonFunctionalRequirementCount, 'requirement', 'requirements')} · ${plural(d.dashboard.userStoryCount, 'user story', 'user stories')}`
        : null;
    case 'validation':
      return d.dashboard ? `${plural(d.dashboard.openClarificationCount, 'open question', 'open questions')} · ${plural(d.dashboard.conflictCount, 'conflict', 'conflicts')}` : null;
    case 'design': {
      const n = count(T.DesignArtifact, T.ApiSpecification, T.DataEntity);
      return n === null ? null : plural(n, 'design artifact', 'design artifacts');
    }
    case 'development': {
      const n = count(T.ImplementationTask);
      return n === null ? null : plural(n, 'implementation task', 'implementation tasks');
    }
    case 'testing': {
      const n = count(T.TestCase);
      if (n === null) return null;
      const frs = d.dashboard?.functionalRequirementCount ?? 0;
      return frs > 0 ? `${plural(n, 'test case', 'test cases')} · ${d.dashboard?.testedFunctionalRequirementCount ?? 0}/${frs} requirements tested` : plural(n, 'test case', 'test cases');
    }
    case 'traceability':
      if (!d.matrix) return null;
      if (d.matrix.length === 0) return 'No requirements to trace yet';
      return `${d.matrix.filter((r) => r.testCases.length > 0).length} of ${d.matrix.length} requirements linked to tests`;
    case 'copilot':
      return 'Ask anything about this project';
    case 'review':
      return 'Read-only project summary';
  }
}

/**
 * The project overview's map of all nine stages: icon, name, plain purpose, derived status, one
 * real figure, and a link. The stage(s) the current work happens in are marked "You are here".
 */
export function ProjectJourney({ projectId, status, signals, data }: { projectId: string; status: number; signals: StageSignals | null; data: JourneyData }) {
  const now = new Set(activeStages(status).map((s) => s.key));
  return (
    <section className="panel journey" aria-labelledby="journey-title">
      <header className="panel-head">
        <div>
          <span className="eyebrow">Project journey</span>
          <h2 id="journey-title">The {STAGE_COUNT} stages of this project</h2>
          <p>Each stage is a part of the work. Open any stage at any time — the highlighted one is where the project’s work is happening now.</p>
        </div>
      </header>
      <ol className="journey-grid">
        {STAGES.map((s) => {
          const st = stageStatus(s, status, signals);
          const here = now.has(s.key);
          const gaps = st === 'attention' ? stageGaps(s, status, signals).length : 0;
          const figure = gaps > 0 ? `${plural(gaps, 'gap', 'gaps')} to resolve` : stageFigure(s.key, data);
          return (
            <li key={s.key} className={`journey-card st-${st}${here ? ' here' : ''}${s.kind === 'tool' ? ' tool' : ''}`}>
              <Link to={phasePath(projectId, s.segment)} className="journey-link">
                <span className="journey-top">
                  <span className="journey-icon" aria-hidden="true">
                    <StageIcon stage={s.key} width={18} height={18} />
                  </span>
                  <span className="journey-num" aria-hidden="true">
                    {s.number}
                  </span>
                  {here && <span className="journey-here">You are here</span>}
                </span>
                <span className="journey-name">
                  <span className="sr-only">Stage {s.number}: </span>
                  {s.name}
                </span>
                <span className="journey-summary">{s.summary}</span>
                <span className="journey-foot">
                  <StageStatusPill status={st} />
                  {figure && <span className={`journey-figure${gaps > 0 ? ' warn' : ''}`}>{figure}</span>}
                </span>
                <span className="journey-open" aria-hidden="true">
                  {gaps > 0 ? 'Resolve' : 'Open'} <IconArrowRight width={13} height={13} />
                </span>
              </Link>
            </li>
          );
        })}
      </ol>
    </section>
  );
}
