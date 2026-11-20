import { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { ApiError } from '../api/client';
import { agentsApi } from '../api/agents';
import { requirementSourcesApi } from '../api/requirementSources';
import type { AgentDefinition, AgentRun, AgentRunSummary, AgentStep, RequirementSource } from '../api/types';
import { roleLabel, stageLabel } from '../lib/lifecycle';
import { formatRelativeTime } from '../lib/format';
import { RUN_STATUS, STEP_STATUS } from '../lib/agentStatus';
import { personLabel, useMemberNames } from '../hooks/useMemberNames';
import { IconAlert, IconSparkles } from './icons';

const REVIEW_AGENT = 5; // AgentKind.Review
const WRITER_ROLES = ['Administrator', 'BusinessAnalyst'];

function errorMessage(err: unknown, fallback: string): string {
  if (!(err instanceof ApiError)) return fallback;
  try {
    return (JSON.parse(err.message) as { message?: string }).message ?? err.message;
  } catch {
    return err.message || fallback;
  }
}

/**
 * AI activity (§36): the six agents, run as a pipeline of agent steps with a human decision after
 * each. The project's lifecycle stage is shown for context only - agent steps serve lifecycle
 * stages but are never lifecycle stages, and approving a step never moves the project.
 */
export function AgentPipelinePanel({
  projectId,
  refreshKey,
  onChange,
}: {
  projectId: string;
  refreshKey?: number;
  onChange?: () => void;
}) {
  const { token, hasRole, userId } = useAuth();
  const names = useMemberNames(projectId);
  const [sources, setSources] = useState<RequirementSource[]>([]);
  const [runs, setRuns] = useState<AgentRun[]>([]);
  const [definitions, setDefinitions] = useState<AgentDefinition[]>([]);
  const [summary, setSummary] = useState<AgentRunSummary | null>(null);
  const [sourceId, setSourceId] = useState('');
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [s, r, d, sum] = await Promise.all([
        requirementSourcesApi.listForProject(projectId, token),
        agentsApi.listForProject(projectId, token),
        agentsApi.definitions(token),
        agentsApi.summary(projectId, token),
      ]);
      setSources(s);
      setRuns(r);
      setDefinitions(d);
      setSummary(sum);
      setSourceId((current) => current || s[0]?.id || '');
    } catch (err) {
      setError(errorMessage(err, 'Failed to load agent runs.'));
    }
  }, [projectId, token]);

  // refreshKey changes when another panel changes project data (e.g. a requirement source is added).
  useEffect(() => {
    void load();
  }, [load, refreshKey]);

  // One action at a time: agent steps call the AI synchronously and can take a while.
  const act = async (action: () => Promise<AgentRun>) => {
    setBusy(true);
    setError(null);
    try {
      await action();
      setComment('');
      await load();
      onChange?.();
    } catch (err) {
      setError(errorMessage(err, 'Action failed.'));
    } finally {
      setBusy(false);
    }
  };

  const canStart = WRITER_ROLES.some(hasRole);
  const activeRun = runs.find((r) => r.status <= 1 || r.status === 4);
  const projectStage = summary?.currentProjectStatus;
  const sourceLabel = (id: string) => {
    const s = sources.find((x) => x.id === id);
    // A Developer's source list carries no text (S2); name the source by its position instead.
    if (s && !s.rawText) return `Requirement source ${sources.length - sources.indexOf(s)}`;
    return s ? `${s.rawText.slice(0, 60).replace(/\s+/g, ' ')}${s.rawText.length > 60 ? '…' : ''}` : id;
  };

  return (
    <div className="card agent-panel" id="sec-ai-assistance">
      <span className="eyebrow ai-eyebrow">
        <IconSparkles width={12} height={12} /> AI activity
      </span>
      <h2>Agent pipeline</h2>
      <p className="hint">
        Six AI agents draft work for the project's lifecycle stages, one agent step at a time. After every step a person with the right role decides whether the
        pipeline continues. Approving an agent step only lets the pipeline continue: it does not approve artifacts or move the project's lifecycle stage.
        Nothing is built or deployed.
      </p>
      {projectStage != null && (
        <p className="agent-project-stage">
          Project lifecycle stage now: <strong>{stageLabel(projectStage)}</strong>
        </p>
      )}

      {definitions.length > 0 && (
        <details className="agent-map">
          <summary>How the agents map to the lifecycle</summary>
          <ul>
            {definitions.map((d) => (
              <li key={d.agent}>
                <strong>{d.displayName}</strong>
                <span className="agent-serves">
                  {d.lifecycleStages.map((s) => (
                    <span key={s} className="stage-chip">
                      {stageLabel(s)}
                    </span>
                  ))}
                </span>
                <span className="hint">{d.lifecycleContribution}</span>
                <span className="hint">Step decided by: {d.approverRoles.map(roleLabel).join(' or ')}</span>
              </li>
            ))}
          </ul>
        </details>
      )}

      {canStart && !activeRun && (
        <div className="agent-start">
          {sources.length === 0 ? (
            <p className="hint">Add a requirement source above to start a run.</p>
          ) : (
            <>
              <select value={sourceId} onChange={(e) => setSourceId(e.target.value)} disabled={busy}>
                {sources.map((s) => (
                  <option key={s.id} value={s.id}>
                    {sourceLabel(s.id)}
                  </option>
                ))}
              </select>
              <button type="button" className="btn btn-primary" disabled={busy || !sourceId} onClick={() => act(() => agentsApi.start(sourceId, token))}>
                {busy ? 'Running agent…' : 'Start agent run'}
              </button>
            </>
          )}
        </div>
      )}

      {error && <p className="error">{error}</p>}

      {runs.length === 0 && <p className="hint">No agent runs yet.</p>}

      {runs.map((run) => {
        const runSummary = summary?.recentRuns.find((r) => r.runId === run.id);
        return (
          <div key={run.id} className="agent-run">
            <div className="agent-run-header">
              <strong>{sourceLabel(run.requirementSourceId)}</strong>
              <span className={`status-pill tone-${RUN_STATUS[run.status]?.tone ?? 'neutral'}`}>{RUN_STATUS[run.status]?.label}</span>
              <span className="hint">Started {formatRelativeTime(run.startedAt)}</span>
            </div>
            <p className="hint agent-run-context">
              Started at: <strong>{run.projectStatusAtStart != null ? stageLabel(run.projectStatusAtStart) : 'not recorded'}</strong>
              {projectStage != null && (
                <>
                  {' '}· Current project stage: <strong>{stageLabel(projectStage)}</strong>
                </>
              )}
            </p>

            <ol className="agent-stages">
              {run.stages.map((step, i) => (
                <StepRow
                  key={step.id}
                  step={step}
                  number={i + 1}
                  ahead={runSummary?.steps[i]?.aheadOfProjectStage ?? false}
                  startStage={run.projectStatusAtStart}
                  decidedBy={step.decidedByUserId ? personLabel(step.decidedByUserId, null, names, userId) : null}
                  canDecide={step.approverRoles.some(hasRole)}
                  busy={busy}
                  comment={comment}
                  onComment={setComment}
                  onApprove={() => act(() => agentsApi.decide(run.id, true, comment || undefined, token))}
                  onReject={() => act(() => agentsApi.decide(run.id, false, comment || undefined, token))}
                  onRetry={() => act(() => agentsApi.retry(run.id, token))}
                />
              ))}
            </ol>
          </div>
        );
      })}
    </div>
  );
}

function StepRow(props: {
  step: AgentStep;
  number: number;
  ahead: boolean;
  startStage: number | null | undefined;
  decidedBy: string | null;
  canDecide: boolean;
  busy: boolean;
  comment: string;
  onComment: (v: string) => void;
  onApprove: () => void;
  onReject: () => void;
  onRetry: () => void;
}) {
  const { step, canDecide, busy } = props;
  const status = STEP_STATUS[step.status];
  const out = step.output;
  const review = step.agent === REVIEW_AGENT;
  const needsDecision = step.status === 2 || step.status === 5;

  return (
    <li className={`agent-stage${needsDecision ? ' needs-decision' : ''}`}>
      <div className="agent-stage-header">
        <span className="agent-step-number">Agent step {props.number}</span>
        <span className="agent-stage-name">{step.displayName}</span>
        <span className="agent-serves" aria-label="Serves lifecycle stage">
          Serves:{' '}
          {step.lifecycleStages.map((s) => (
            <span key={s} className="stage-chip">
              {stageLabel(s)}
            </span>
          ))}
        </span>
        {/* Relative to the stage the run started at (projectStatusAtStart), not the current stage. */}
        {props.ahead && (
          <span
            className="ai-ahead"
            title={props.startStage != null ? `This step serves a later stage than ${stageLabel(props.startStage)}, where the project was when this run started.` : undefined}
          >
            Ahead of project stage when this run started
          </span>
        )}
        <span className={`status-pill tone-${status?.tone ?? 'neutral'}`}>{status?.label}</span>
      </div>

      {step.decidedAt && (
        <p className="hint">
          Decided {formatRelativeTime(step.decidedAt)}
          {props.decidedBy && props.decidedBy !== 'Someone' ? ` by ${props.decidedBy}` : ''}
          {step.decisionComment && ` · “${step.decisionComment}”`}
        </p>
      )}

      {out && (
        <div className="agent-stage-body">
          {review && (
            <p className="agent-review-note">
              <IconAlert width={13} height={13} /> AI review identifies readiness and evidence issues. Human lifecycle approval is still required: this report
              approves nothing and does not move the project.
            </p>
          )}
          <p>{out.summary}</p>
          {Object.keys(out.metrics).length > 0 && (
            <p className="hint">
              {Object.entries(out.metrics)
                .map(([k, v]) => `${k}: ${v}`)
                .join(' · ')}
            </p>
          )}
          {out.producedArtifacts.length > 0 && <p className="hint">Drafted (AI proposals, awaiting human approval): {out.producedArtifacts.map((a) => a.code).join(', ')}</p>}
          {out.findings.length > 0 && (
            <>
              <p className="agent-findings-title">{review ? 'Lifecycle evidence findings' : 'Findings'}</p>
              <ul className="agent-findings">
                {out.findings.map((f, i) => (
                  <li key={i}>{f}</li>
                ))}
              </ul>
            </>
          )}
          {step.status === 2 && <p className="hint">{out.reviewGuidance}</p>}
        </div>
      )}

      {step.error && <p className="error">{step.error}</p>}

      {needsDecision && (
        <div className="agent-stage-actions">
          {canDecide ? (
            <>
              <input type="text" placeholder="Optional comment" value={props.comment} onChange={(e) => props.onComment(e.target.value)} disabled={busy} />
              {step.status === 2 && (
                <button type="button" className="btn btn-primary" disabled={busy} onClick={props.onApprove}>
                  {busy ? 'Working…' : review ? 'Approve step & finish run' : 'Approve step & continue'}
                </button>
              )}
              {step.status === 5 && (
                <button type="button" className="btn btn-primary" disabled={busy} onClick={props.onRetry}>
                  {busy ? 'Working…' : 'Retry step'}
                </button>
              )}
              <button type="button" className="btn btn-secondary btn-reject" disabled={busy} onClick={props.onReject}>
                {step.status === 5 ? 'Abandon run' : 'Reject step'}
              </button>
            </>
          ) : (
            <p className="hint">Human decision needed from: {step.approverRoles.map(roleLabel).join(' or ')}</p>
          )}
        </div>
      )}
    </li>
  );
}
