import { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { ApiError } from '../api/client';
import { agentsApi } from '../api/agents';
import { requirementSourcesApi } from '../api/requirementSources';
import type { AgentRun, AgentStage, RequirementSource } from '../api/types';

const RUN_STATUS: Record<number, { label: string; tone: string }> = {
  0: { label: 'Running', tone: 'info' },
  1: { label: 'Awaiting approval', tone: 'warning' },
  2: { label: 'Completed', tone: 'success' },
  3: { label: 'Rejected', tone: 'danger' },
  4: { label: 'Failed', tone: 'danger' },
};

const STAGE_STATUS: Record<number, { label: string; tone: string }> = {
  0: { label: 'Pending', tone: 'neutral' },
  1: { label: 'Running', tone: 'info' },
  2: { label: 'Awaiting approval', tone: 'warning' },
  3: { label: 'Approved', tone: 'success' },
  4: { label: 'Rejected', tone: 'danger' },
  5: { label: 'Failed', tone: 'danger' },
};

const WRITER_ROLES = ['Administrator', 'BusinessAnalyst'];

function errorMessage(err: unknown, fallback: string): string {
  if (!(err instanceof ApiError)) return fallback;
  try {
    return (JSON.parse(err.message) as { message?: string }).message ?? err.message;
  } catch {
    return err.message || fallback;
  }
}

export function AgentPipelinePanel({
  projectId,
  refreshKey,
  onChange,
}: {
  projectId: string;
  refreshKey?: number;
  onChange?: () => void;
}) {
  const { token, hasRole } = useAuth();
  const [sources, setSources] = useState<RequirementSource[]>([]);
  const [runs, setRuns] = useState<AgentRun[]>([]);
  const [sourceId, setSourceId] = useState('');
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [s, r] = await Promise.all([
        requirementSourcesApi.listForProject(projectId, token),
        agentsApi.listForProject(projectId, token),
      ]);
      setSources(s);
      setRuns(r);
      setSourceId((current) => current || s[0]?.id || '');
    } catch (err) {
      setError(errorMessage(err, 'Failed to load agent runs.'));
    }
  }, [projectId, token]);

  // refreshKey changes when another panel changes project data (e.g. a requirement source is added).
  useEffect(() => {
    void load();
  }, [load, refreshKey]);

  // One action at a time: agent stages call the AI synchronously and can take a while.
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
  const sourceLabel = (id: string) => {
    const s = sources.find((x) => x.id === id);
    return s ? `${s.rawText.slice(0, 60).replace(/\s+/g, ' ')}${s.rawText.length > 60 ? '…' : ''}` : id;
  };

  return (
    <div className="card" id="sec-ai-assistance">
      <h2>Agent Pipeline</h2>
      <p className="hint">
        §36 — Requirements → Analysis → Architecture → Development Planning → QA → Review. Each agent runs only after a
        human with the right role approves the previous stage. Agents plan and document; nothing is built or deployed.
      </p>

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
              <button type="button" disabled={busy || !sourceId} onClick={() => act(() => agentsApi.start(sourceId, token))}>
                {busy ? 'Running agent…' : 'Start agent run'}
              </button>
            </>
          )}
        </div>
      )}

      {error && <p className="error">{error}</p>}

      {runs.length === 0 && <p className="hint">No agent runs yet.</p>}

      {runs.map((run) => (
        <div key={run.id} className="agent-run">
          <div className="agent-run-header">
            <strong>{sourceLabel(run.requirementSourceId)}</strong>
            <span className={`status-pill tone-${RUN_STATUS[run.status]?.tone ?? 'neutral'}`}>
              {RUN_STATUS[run.status]?.label}
            </span>
            <span className="hint">{new Date(run.startedAt).toLocaleString()}</span>
          </div>

          <ol className="agent-stages">
            {run.stages.map((stage) => (
              <StageRow
                key={stage.id}
                stage={stage}
                canDecide={stage.approverRoles.some(hasRole)}
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
      ))}
    </div>
  );
}

function StageRow(props: {
  stage: AgentStage;
  canDecide: boolean;
  busy: boolean;
  comment: string;
  onComment: (v: string) => void;
  onApprove: () => void;
  onReject: () => void;
  onRetry: () => void;
}) {
  const { stage, canDecide, busy } = props;
  const status = STAGE_STATUS[stage.status];
  const out = stage.output;

  return (
    <li className="agent-stage">
      <div className="agent-stage-header">
        <span className="agent-stage-name">{stage.displayName}</span>
        <span className={`status-pill tone-${status?.tone ?? 'neutral'}`}>{status?.label}</span>
      </div>

      {out && (
        <div className="agent-stage-body">
          <p>{out.summary}</p>
          {Object.keys(out.metrics).length > 0 && (
            <p className="hint">
              {Object.entries(out.metrics)
                .map(([k, v]) => `${k}: ${v}`)
                .join(' · ')}
            </p>
          )}
          {out.producedArtifacts.length > 0 && (
            <p className="hint">Produced: {out.producedArtifacts.map((a) => a.code).join(', ')}</p>
          )}
          {out.findings.length > 0 && (
            <ul className="agent-findings">
              {out.findings.map((f, i) => (
                <li key={i}>{f}</li>
              ))}
            </ul>
          )}
          {stage.status === 2 && <p className="hint">{out.reviewGuidance}</p>}
        </div>
      )}

      {stage.error && <p className="error">{stage.error}</p>}

      {stage.decisionComment && <p className="hint">Decision note: {stage.decisionComment}</p>}

      {(stage.status === 2 || stage.status === 5) && (
        <div className="agent-stage-actions">
          {canDecide ? (
            <>
              <input
                type="text"
                placeholder="Optional comment"
                value={props.comment}
                onChange={(e) => props.onComment(e.target.value)}
                disabled={busy}
              />
              {stage.status === 2 && (
                <button type="button" disabled={busy} onClick={props.onApprove}>
                  {busy ? 'Working…' : 'Approve & continue'}
                </button>
              )}
              {stage.status === 5 && (
                <button type="button" disabled={busy} onClick={props.onRetry}>
                  {busy ? 'Working…' : 'Retry stage'}
                </button>
              )}
              <button type="button" className="secondary" disabled={busy} onClick={props.onReject}>
                {stage.status === 5 ? 'Abandon run' : 'Reject'}
              </button>
            </>
          ) : (
            <p className="hint">Waiting for: {stage.approverRoles.join(' or ')}</p>
          )}
        </div>
      )}
    </li>
  );
}
