import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { traceabilityApi } from '../api/traceability';
import type { TraceRef, TraceabilityRow } from '../api/types';
import { testCoveragePercent } from '../lib/coverage';
import { phasePath } from '../lib/sdlc';
import { EmptyState } from './ui/EmptyState';
import { ErrorState } from './ui/ErrorState';
import { IconArrowRight, IconLink, IconRefresh } from './icons';

// §21 chain: Business Objective -> Requirement -> Story -> Acceptance Criteria -> Design -> Task -> Test.
// The objective column is rendered before the requirement column; the rest follow it.
const COLUMNS: { key: keyof Omit<TraceabilityRow, 'functionalRequirementId' | 'functionalRequirementCode' | 'functionalRequirementTitle' | 'businessObjectives'>; label: string }[] = [
  { key: 'businessRules', label: 'Business Rules' },
  { key: 'userStories', label: 'User Stories' },
  { key: 'acceptanceCriteria', label: 'Acceptance Criteria' },
  { key: 'designArtifacts', label: 'Design' },
  { key: 'dataEntities', label: 'Data' },
  { key: 'apiSpecifications', label: 'API' },
  { key: 'implementationTasks', label: 'Tasks' },
  { key: 'testCases', label: 'Tests' },
];

function Cell({ refs }: { refs: TraceRef[] }) {
  if (refs.length === 0) return <td className="trace-empty" aria-label="None">—</td>;
  return (
    <td>
      <div className="trace-chips">
        {refs.map((r) => (
          <span key={r.id} className="code trace-chip" title={r.title}>
            {r.code}
          </span>
        ))}
      </div>
    </td>
  );
}

/** Live traceability matrix: one row per functional requirement, with the flow Requirement → Design → Development → Test above it. */
export function TraceabilityMatrix({ projectId }: { projectId: string }) {
  const { token } = useAuth();
  const [rows, setRows] = useState<TraceabilityRow[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    setBusy(true);
    setFailed(false);
    try {
      setRows(await traceabilityApi.getMatrix(projectId, token));
    } catch {
      setFailed(true);
    } finally {
      setBusy(false);
    }
  }, [projectId, token]);

  useEffect(() => {
    void load();
  }, [load]);

  if (failed) return <ErrorState title="Unable to load the traceability matrix" description="We couldn't load traceability data right now." onRetry={load} />;
  if (rows === null) return <div className="skeleton skeleton-panel" />;
  if (rows.length === 0) {
    return (
      <EmptyState
        icon={<IconLink width={20} height={20} />}
        title="Nothing to trace yet"
        description="Traceability follows each functional requirement through design, implementation and tests. Generate functional requirements first."
        action={
          <Link to={phasePath(projectId, 'analysis')} className="btn btn-primary">
            Open Requirements Analysis
          </Link>
        }
      />
    );
  }

  const withAny = (pick: (r: TraceabilityRow) => TraceRef[]) => rows.filter((r) => pick(r).length > 0).length;
  const flow = [
    { label: 'Requirements', count: rows.length, covered: rows.length, pct: 100 },
    { label: 'Design', count: rows.reduce((n, r) => n + r.designArtifacts.length, 0), covered: withAny((r) => r.designArtifacts), pct: Math.round((withAny((r) => r.designArtifacts) / rows.length) * 100) },
    {
      label: 'Development',
      count: rows.reduce((n, r) => n + r.implementationTasks.length, 0),
      covered: withAny((r) => r.implementationTasks),
      pct: Math.round((withAny((r) => r.implementationTasks) / rows.length) * 100),
    },
    { label: 'Test cases', count: rows.reduce((n, r) => n + r.testCases.length, 0), covered: withAny((r) => r.testCases), pct: testCoveragePercent(rows) },
  ];

  return (
    <>
      <ol className="trace-flow" aria-label="Traceability flow">
        {flow.map((f, i) => (
          <li key={f.label}>
            <div className="trace-flow-node">
              <span className="trace-flow-label">{f.label}</span>
              <strong>{f.count}</strong>
              <span className="trace-flow-cov">{i === 0 ? 'functional requirements' : `${f.covered} of ${rows.length} requirements covered`}</span>
              <div className="progress-track" aria-hidden="true">
                <div className="progress-fill" style={{ width: `${f.pct}%` }} />
              </div>
            </div>
            {i < flow.length - 1 && <IconArrowRight className="trace-flow-arrow" width={18} height={18} aria-hidden="true" />}
          </li>
        ))}
      </ol>

      <section className="panel">
        <header className="panel-head">
          <div>
            <h2>Traceability matrix</h2>
            <p>Each functional requirement, the business objective it serves, and everything derived from it.</p>
          </div>
          <button type="button" className="btn btn-secondary btn-sm" onClick={load} disabled={busy}>
            <IconRefresh width={14} height={14} /> {busy ? 'Refreshing…' : 'Refresh'}
          </button>
        </header>
        <div className="table-scroll">
          <table className="data-table trace-table">
            <thead>
              <tr>
                <th scope="col">Business Objective</th>
                <th scope="col" className="sticky-col">
                  Requirement
                </th>
                {COLUMNS.map((c) => (
                  <th key={c.key} scope="col">
                    {c.label}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.functionalRequirementId}>
                  <Cell refs={row.businessObjectives ?? []} />
                  <th scope="row" className="sticky-col">
                    <span className="code">{row.functionalRequirementCode}</span> {row.functionalRequirementTitle}
                  </th>
                  {COLUMNS.map((c) => (
                    <Cell key={c.key} refs={row[c.key]} />
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    </>
  );
}
