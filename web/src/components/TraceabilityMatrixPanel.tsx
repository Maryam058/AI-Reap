import { useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { ApiError } from '../api/client';
import { traceabilityApi } from '../api/traceability';
import type { TraceRef, TraceabilityRow } from '../api/types';

function Cell({ refs }: { refs: TraceRef[] }) {
  if (refs.length === 0) return <td className="trace-empty">—</td>;
  return (
    <td>
      {refs.map((r) => (
        <span key={r.id} className="code trace-chip" title={r.title}>
          {r.code}
        </span>
      ))}
    </td>
  );
}

export function TraceabilityMatrixPanel({ projectId }: { projectId: string }) {
  const { token } = useAuth();
  const [rows, setRows] = useState<TraceabilityRow[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = async () => {
    setBusy(true);
    setError(null);
    try {
      setRows(await traceabilityApi.getMatrix(projectId, token));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to load traceability matrix.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="card">
      <h2>Traceability Matrix</h2>
      <p className="hint">§21 — Objective → Requirement → Story → Acceptance Criteria → Design → Task → Test, per functional requirement.</p>
      <button type="button" disabled={busy} onClick={load}>
        {busy ? 'Loading…' : rows ? 'Refresh' : 'Load matrix'}
      </button>

      {error && <p className="error">{error}</p>}

      {rows && (
        rows.length === 0 ? (
          <p>No functional requirements yet.</p>
        ) : (
          <div className="table-scroll">
            <table className="trace-table">
              <thead>
                <tr>
                  <th>Requirement</th>
                  <th>Business Rules</th>
                  <th>User Stories</th>
                  <th>Acceptance Criteria</th>
                  <th>Design</th>
                  <th>Data Entities</th>
                  <th>API Specs</th>
                  <th>Tasks</th>
                  <th>Test Cases</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((row) => (
                  <tr key={row.functionalRequirementId}>
                    <td>
                      <span className="code">{row.functionalRequirementCode}</span> {row.functionalRequirementTitle}
                    </td>
                    <Cell refs={row.businessRules} />
                    <Cell refs={row.userStories} />
                    <Cell refs={row.acceptanceCriteria} />
                    <Cell refs={row.designArtifacts} />
                    <Cell refs={row.dataEntities} />
                    <Cell refs={row.apiSpecifications} />
                    <Cell refs={row.implementationTasks} />
                    <Cell refs={row.testCases} />
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )
      )}
    </div>
  );
}
