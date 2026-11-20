import { useEffect, useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { workRecordsApi } from '../api/workRecords';
import type { ArtifactSummary, ArtifactWorkRecord } from '../api/types';
import { isExecutable, openPath, summarizeQa, type QaState, type QaSummaryItem } from '../lib/testQaSummary';
import { IconAlert, IconArrowRight, IconCheck, IconCheckCircle, IconClock, IconShieldCheck } from './icons';

const VISIBLE_ITEMS = 8;

const TONE: Record<QaState, string> = {
  passed: 'success',
  failed: 'danger',
  blocked: 'blocked',
  notExecuted: 'neutral',
  needsApproval: 'info',
  unavailable: 'neutral',
};

/**
 * QA test results for the whole project, at the top of the Testing page: how many test cases QA
 * has passed and which ones still need something, so nobody has to open each card to find out.
 * Read-only. Results come from each test case's existing work records under the existing evidence
 * rule (lib/testQaSummary); "Open" goes to the test case's card, where the work itself is done.
 */
export function TestQaSummary({ projectId, testCases }: { projectId: string; testCases: ArtifactSummary[] }) {
  const { token } = useAuth();
  // The last loaded snapshot: the test cases and the work records of the executable ones (a missing
  // entry means that request failed). It stays on screen while a refresh loads, so nothing flickers.
  const [loaded, setLoaded] = useState<{ testCases: ArtifactSummary[]; records: Map<string, ArtifactWorkRecord[]> } | null>(null);

  useEffect(() => {
    let cancelled = false;
    const executable = testCases.filter((t) => isExecutable(t.status));
    Promise.allSettled(executable.map((t) => workRecordsApi.list(t.id, token))).then((results) => {
      if (cancelled) return;
      const records = new Map<string, ArtifactWorkRecord[]>();
      results.forEach((r, i) => r.status === 'fulfilled' && records.set(executable[i].id, r.value));
      setLoaded({ testCases, records });
    });
    return () => {
      cancelled = true;
    };
  }, [testCases, token]);

  if (testCases.length === 0) {
    return (
      <section className="card qa-summary" aria-label="QA test results">
        <h2 className="qa-summary-title">
          <IconShieldCheck width={18} height={18} /> QA test results
        </h2>
        <p className="muted qa-summary-empty">No test cases yet. Generate test cases from the functional requirements below; their QA results will be summarised here.</p>
      </section>
    );
  }

  if (loaded === null) {
    return (
      <section className="card qa-summary" aria-label="QA test results" aria-busy="true">
        <h2 className="qa-summary-title">
          <IconShieldCheck width={18} height={18} /> QA test results
        </h2>
        <p className="muted">Loading test results…</p>
      </section>
    );
  }

  const s = summarizeQa(loaded.testCases, loaded.records);
  const allPassed = s.counts.passed === s.total;
  const chips: { state: QaState; label: string; icon: ReactNode; always?: boolean }[] = [
    { state: 'passed', label: 'Passed', icon: <IconCheck width={12} height={12} />, always: true },
    { state: 'failed', label: 'Failed', icon: <IconAlert width={12} height={12} />, always: true },
    { state: 'blocked', label: 'Blocked', icon: <IconAlert width={12} height={12} />, always: true },
    { state: 'notExecuted', label: 'Not executed', icon: <IconClock width={12} height={12} />, always: true },
    { state: 'needsApproval', label: 'Needs approval', icon: <IconClock width={12} height={12} /> },
    { state: 'unavailable', label: 'Result unavailable', icon: <IconAlert width={12} height={12} /> },
  ];

  return (
    <section className="card qa-summary" aria-label="QA test results">
      <div className="qa-summary-head">
        <h2 className="qa-summary-title">
          <IconShieldCheck width={18} height={18} /> QA test results
        </h2>
        <p className="qa-summary-score">
          <strong>
            {s.counts.passed} / {s.total}
          </strong>{' '}
          passed by QA
        </p>
      </div>

      <div className="qa-summary-meter" role="img" aria-label={`${s.counts.passed} of ${s.total} test cases passed by QA`}>
        {chips.map(({ state }) =>
          s.counts[state] > 0 ? <span key={state} className={`qa-meter-seg tone-${TONE[state]}`} style={{ flexGrow: s.counts[state] }} /> : null,
        )}
      </div>

      <ul className="qa-summary-counts">
        {chips
          .filter((c) => c.always || s.counts[c.state] > 0)
          .map((c) => (
            <li key={c.state} className={`status-pill tone-${TONE[c.state]}`}>
              {c.icon} {c.label}: {s.counts[c.state]}
            </li>
          ))}
      </ul>

      {allPassed ? (
        <p className="qa-summary-done">
          <IconCheckCircle width={16} height={16} /> All test cases have been passed by QA.
        </p>
      ) : (
        <div className="qa-attention">
          <h3 className="qa-attention-title">Needs attention — {s.attentionCount}</h3>
          <p className="hint">Only a result recorded by QA counts. Administrator records are kept for audit but are not QA evidence.</p>
          {s.attention.map((g) => (
            <AttentionGroup key={g.state} projectId={projectId} title={g.title} items={g.items} />
          ))}
        </div>
      )}
    </section>
  );
}

function AttentionGroup({ projectId, title, items }: { projectId: string; title: string; items: QaSummaryItem[] }) {
  const [showAll, setShowAll] = useState(false);
  const shown = showAll ? items : items.slice(0, VISIBLE_ITEMS);
  return (
    <section className="qa-attention-group" aria-label={title}>
      <h4>
        {title} <span className="muted">· {items.length}</span>
      </h4>
      <ul>
        {shown.map(({ testCase, state, label, administratorRecordOnly }) => (
          <li key={testCase.id} className="qa-attention-item">
            <span className="code">{testCase.code}</span>
            <span className="qa-attention-name" title={testCase.title}>
              {testCase.title}
            </span>
            <span className={`status-pill tone-${TONE[state]}`}>{label}</span>
            {administratorRecordOnly && <span className="muted qa-attention-note">Latest record by Administrator (not evidence)</span>}
            <Link to={openPath(projectId, testCase)} className="btn btn-secondary btn-sm qa-attention-open" aria-label={`Open ${testCase.code}`}>
              Open <IconArrowRight width={13} height={13} />
            </Link>
          </li>
        ))}
      </ul>
      {items.length > VISIBLE_ITEMS && (
        <button type="button" className="btn btn-ghost btn-sm" onClick={() => setShowAll((v) => !v)} aria-expanded={showAll}>
          {showAll ? 'Show fewer' : `Show all ${items.length}`}
        </button>
      )}
    </section>
  );
}
