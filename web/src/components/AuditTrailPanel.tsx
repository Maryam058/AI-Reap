import { useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { ApiError } from '../api/client';
import { auditApi } from '../api/audit';
import type { AiExecution } from '../api/types';

export function AuditTrailPanel({ projectId }: { projectId: string }) {
  const { token } = useAuth();
  const [executions, setExecutions] = useState<AiExecution[] | null>(null);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = async () => {
    setBusy(true);
    setError(null);
    try {
      setExecutions(await auditApi.getForProject(projectId, token));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to load audit trail.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="card" id="ai-audit-trail">
      <h2>AI Audit Trail</h2>
      <p className="hint">§27 — every AI call this project has made. No hidden provider reasoning is ever stored — only structured input/output.</p>
      <button type="button" disabled={busy} onClick={load}>
        {busy ? 'Loading…' : executions ? 'Refresh' : 'Load audit trail'}
      </button>

      {error && <p className="error">{error}</p>}

      {executions && (
        executions.length === 0 ? (
          <p>No AI operations recorded yet.</p>
        ) : (
          <ul className="audit-list">
            {executions.map((e) => (
              <li key={e.id}>
                <div className="audit-row" onClick={() => setExpandedId(expandedId === e.id ? null : e.id)}>
                  <span className="audit-op">{e.operationType}</span>
                  <span className="hint">{e.model}</span>
                  <span className="hint">{new Date(e.timestamp).toLocaleString()}</span>
                </div>
                {expandedId === e.id && (
                  <div className="audit-detail">
                    <p className="hint">Input reference: {e.inputReference ?? '—'}</p>
                    <p className="hint">Prompt template version: {e.promptTemplateVersion ?? '—'}</p>
                    <p className="hint">
                      Human decision: {e.accepted === true ? 'Accepted' : e.accepted === false ? 'Rejected' : 'Not yet reviewed'}
                      {e.producedArtifactId ? ` (artifact ${e.producedArtifactId})` : ''}
                    </p>
                    <pre>{e.outputJson}</pre>
                  </div>
                )}
              </li>
            ))}
          </ul>
        )
      )}
    </div>
  );
}
