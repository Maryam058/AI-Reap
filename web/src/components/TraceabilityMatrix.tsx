import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { traceabilityApi } from '../api/traceability';
import { artifactsApi } from '../api/artifacts';
import { ARTIFACT_STATUS, type TraceRef, type TraceabilityGaps, type TraceabilityOrphan, type TraceabilityRow } from '../api/types';
import { apiErrorText } from '../lib/errors';
import { testCoveragePercent } from '../lib/coverage';
import { artifactTypeName } from '../lib/lifecycleReview';
import { gapsOf, linkCandidates, rowHasGap } from '../lib/traceabilityGaps';
import { phasePath } from '../lib/workAreas';
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

function GapCodes({ refs }: { refs: { id: string; code: string; title: string }[] }) {
  return (
    <span className="trace-chips trace-gap-chips">
      {refs.map((r) => (
        <span key={r.id} className="code trace-chip" title={r.title}>
          {r.code}
        </span>
      ))}
    </span>
  );
}

function OrphanLinker({ orphan, candidates, onLinked }: { orphan: TraceabilityOrphan; candidates: TraceRef[]; onLinked: () => void }) {
  const { token } = useAuth();
  const [target, setTarget] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const link = async () => {
    setSaving(true);
    setError(null);
    try {
      await traceabilityApi.linkToRequirement(orphan.id, target, token);
      onLinked();
    } catch (err) {
      setError(apiErrorText(err, 'The link could not be saved.'));
    } finally {
      setSaving(false);
    }
  };
  return (
    <li className="trace-link-row">
      <span className="code">{orphan.code}</span>
      <select className="select-input" value={target} onChange={(e) => setTarget(e.target.value)} aria-label={`${orphan.missingLink} to link ${orphan.code} to`} disabled={saving || candidates.length === 0}>
        <option value="">{candidates.length === 0 ? `No ${orphan.missingLink} to link to yet` : `Link to a ${orphan.missingLink}…`}</option>
        {candidates.map((c) => (
          <option key={c.id} value={c.id}>
            {c.code} — {c.title}
          </option>
        ))}
      </select>
      <button type="button" className="btn btn-secondary btn-sm" onClick={link} disabled={!target || saving}>
        {saving ? 'Linking…' : 'Link'}
      </button>
      {error && <span className="field-error-text">{error}</span>}
    </li>
  );
}

/** Live traceability matrix: one row per functional requirement, with the flow Requirement → Design → Development → Test above it. */
export function TraceabilityMatrix({ projectId }: { projectId: string }) {
  const { token, hasRole } = useAuth();
  // The same roles the link endpoint allows (TraceabilityController.WriterRoles).
  const canLink = hasRole('Administrator') || hasRole('BusinessAnalyst');
  const [rows, setRows] = useState<TraceabilityRow[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [busy, setBusy] = useState(false);
  // Loaded beside the matrix; a failure here never hides the matrix (null = not loaded, 'failed' = unavailable).
  const [gaps, setGaps] = useState<TraceabilityGaps | null | 'failed'>(null);
  const [rejected, setRejected] = useState<ReadonlySet<string>>(new Set());
  const [onlyGaps, setOnlyGaps] = useState(false);

  const load = useCallback(async () => {
    setBusy(true);
    setFailed(false);
    const gapsRequest = traceabilityApi.getGaps(projectId, token).then(setGaps, () => setGaps('failed'));
    // Rejected artifacts are never offered as link targets (the server refuses them); without the list
    // nothing is filtered and a refusal is shown on the row, as before.
    void artifactsApi.listForProject(projectId, token).then(
      (all) => setRejected(new Set(all.filter((a) => a.status === ARTIFACT_STATUS.Rejected).map((a) => a.id))),
      () => setRejected(new Set()),
    );
    try {
      setRows(await traceabilityApi.getMatrix(projectId, token));
    } catch {
      setFailed(true);
    } finally {
      await gapsRequest;
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

  const summary = gapsOf(rows, gaps === 'failed' ? null : gaps);
  const shownRows = onlyGaps ? rows.filter(rowHasGap) : rows;

  return (
    <>
      <section className="panel" aria-labelledby="trace-gaps-title">
        <header className="panel-head">
          <div>
            <h2 id="trace-gaps-title">Coverage gaps</h2>
            <p>Requirements that nothing is built or tested from yet, and work that is not traced back to any requirement.</p>
          </div>
        </header>
        {summary.total === 0 && gaps !== 'failed' ? (
          <p className="muted">{gaps === null ? 'Checking for gaps…' : 'No gaps: every requirement has stories and tests, and all work traces back to a requirement.'}</p>
        ) : (
          <ul className="trace-gap-list">
            {summary.withoutStories.length > 0 && (
              <li>
                <strong>{summary.withoutStories.length}</strong> functional requirement{summary.withoutStories.length === 1 ? ' has' : 's have'} no user story:{' '}
                <GapCodes refs={summary.withoutStories} />
              </li>
            )}
            {summary.withoutTests.length > 0 && (
              <li>
                <strong>{summary.withoutTests.length}</strong> functional requirement{summary.withoutTests.length === 1 ? ' has' : 's have'} no test case:{' '}
                <GapCodes refs={summary.withoutTests} />
              </li>
            )}
            {summary.orphansByType.map(([type, list]) => (
              <li key={type}>
                <strong>{list.length}</strong> {artifactTypeName(type, list.length !== 1).toLowerCase()} not linked to any {list[0].missingLink}:{' '}
                <GapCodes refs={list} />
                {canLink && (
                  <ul className="trace-link-list">
                    {list.map((o) => (
                      <OrphanLinker key={o.id} orphan={o} candidates={linkCandidates(o, rows, gaps === 'failed' ? null : gaps, rejected)} onLinked={load} />
                    ))}
                  </ul>
                )}
              </li>
            ))}
            {summary.untracedNfrs.length > 0 && (
              <li>
                <strong>{summary.untracedNfrs.length}</strong> of {summary.nfrCount} non-functional requirement{summary.nfrCount === 1 ? '' : 's'} not traced to any
                story, design, task or test: <GapCodes refs={summary.untracedNfrs} />
              </li>
            )}
            {gaps === 'failed' && <li className="muted">Work not linked to a requirement could not be checked right now; use Refresh to try again.</li>}
          </ul>
        )}
      </section>

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
            <p>
              Each row is one functional requirement: the business objective it serves, and everything built from it — stories, design, tasks and tests. An empty cell
              means nothing is linked yet.
            </p>
          </div>
          <div className="trace-actions">
            <label className="trace-gap-toggle">
              <input type="checkbox" checked={onlyGaps} onChange={(e) => setOnlyGaps(e.target.checked)} /> Only requirements with gaps
            </label>
            <button type="button" className="btn btn-secondary btn-sm" onClick={load} disabled={busy}>
              <IconRefresh width={14} height={14} /> {busy ? 'Refreshing…' : 'Refresh'}
            </button>
          </div>
        </header>
        {onlyGaps && shownRows.length === 0 && <p className="muted">Every functional requirement has at least one story, task and test case.</p>}
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
              {shownRows.map((row) => (
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
