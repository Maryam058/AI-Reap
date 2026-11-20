import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { agentsApi } from '../api/agents';
import type { AgentRunSummary } from '../api/types';
import { roleLabel, stageLabel, stageState } from '../lib/lifecycle';
import { formatRelativeTime } from '../lib/format';
import { phasePath } from '../lib/workAreas';
import { RUN_STATUS } from '../lib/agentStatus';
import { EmptyState } from './ui/EmptyState';
import { IconSparkles } from './icons';

/**
 * AI activity on the project overview, deliberately below and apart from the lifecycle: which
 * lifecycle stages the agents have been drafting for, and any agent step waiting for a person.
 * Read-only (GET .../agent-runs/summary); agent runs never move the project's stage.
 */
export function AgentActivitySummary({ projectId, version }: { projectId: string; version?: unknown }) {
  const { token } = useAuth();
  const [summary, setSummary] = useState<AgentRunSummary | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    agentsApi
      .summary(projectId, token)
      .then((s) => {
        if (cancelled) return;
        setSummary(s);
        setFailed(false);
      })
      .catch(() => !cancelled && setFailed(true));
    return () => {
      cancelled = true;
    };
  }, [projectId, token, version]);

  const pipeline = `${phasePath(projectId, 'copilot')}?tab=pipeline`;
  const latest = summary?.recentRuns[0];
  const served = summary?.byLifecycleStage.filter((s) => s.agents.length > 0) ?? [];

  return (
    <section className="panel ai-activity" aria-labelledby="ai-activity-title">
      <header className="panel-head">
        <div>
          <span className="eyebrow ai-eyebrow">
            <IconSparkles width={12} height={12} /> AI activity
          </span>
          <h2 id="ai-activity-title">Agent work for the lifecycle</h2>
          <p>Agents draft work that serves lifecycle stages. They never move the project; people approve their output.</p>
        </div>
        <Link to={pipeline} className="btn btn-ghost btn-sm">
          Open agent pipeline
        </Link>
      </header>

      {failed && !summary ? (
        <p className="muted">AI activity is unavailable right now.</p>
      ) : !summary ? (
        <div className="skeleton skeleton-line" />
      ) : summary.totalRuns === 0 ? (
        <EmptyState compact icon={<IconSparkles width={18} height={18} />} title="No agent runs yet" description="Start an agent run from the agent pipeline to draft requirements, design, plans and tests." />
      ) : (
        <>
          <ul className="ai-stage-strip" aria-label="Agent steps by the lifecycle stage they serve">
            {served.map((s) => {
              const state = stageState(s.stage, summary.currentProjectStatus);
              return (
                <li key={s.stage} className={`ai-stage ${state}`}>
                  <span className="ai-stage-name">{s.label}</span>
                  <span className="ai-stage-agents">{s.agents.join(', ')}</span>
                  <span className="ai-stage-counts">
                    {s.stepsApproved} approved
                    {s.stepsAwaitingDecision > 0 && <strong> · {s.stepsAwaitingDecision} awaiting</strong>}
                    {s.stepsRejected > 0 && ` · ${s.stepsRejected} rejected`}
                    {s.stepsFailed > 0 && ` · ${s.stepsFailed} failed`}
                  </span>
                  {state === 'upcoming' && s.stepsApproved + s.stepsAwaitingDecision > 0 && <span className="ai-ahead">Ahead of project stage</span>}
                </li>
              );
            })}
          </ul>
          {latest && (
            <p className="ai-latest">
              Latest run <span className={`status-pill tone-${RUN_STATUS[latest.status]?.tone ?? 'neutral'}`}>{RUN_STATUS[latest.status]?.label}</span> started{' '}
              {formatRelativeTime(latest.startedAt)}
              {latest.projectStatusAtStart != null && <> while the project was in {stageLabel(latest.projectStatusAtStart)}</>}.{' '}
              {latest.awaitingHumanDecision && latest.currentStep && (
                <strong>
                  {latest.currentStep.displayName} step needs a decision by {latest.currentStep.approverRoles.map(roleLabel).join(' or ')}.
                </strong>
              )}
            </p>
          )}
        </>
      )}
    </section>
  );
}
