import { useEffect, useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { artifactsApi } from '../api/artifacts';
import { traceabilityApi } from '../api/traceability';
import {
  ARTIFACT_TYPE_VALUES,
  RELATIONSHIP_TYPE_LABELS,
  type ArtifactRelationship,
  type ArtifactVersion,
  type ImpactAnalysisResult,
} from '../api/types';

interface DiffRow {
  key: string;
  oldValue: string;
  newValue: string;
  changed: boolean;
}

function toDisplayValue(value: unknown): string {
  if (value === undefined || value === null) return '—';
  if (typeof value === 'string') return value;
  return JSON.stringify(value);
}

// §23 — field-by-field diff between two version snapshots' data payloads. Entirely
// client-side: the versions list already carries each version's full data snapshot, so no
// dedicated diff endpoint is needed.
function diffVersions(a: Record<string, unknown>, b: Record<string, unknown>): DiffRow[] {
  const keys = Array.from(new Set([...Object.keys(a), ...Object.keys(b)])).sort();
  return keys.map((key) => {
    const oldValue = toDisplayValue(a[key]);
    const newValue = toDisplayValue(b[key]);
    return { key, oldValue, newValue, changed: oldValue !== newValue };
  });
}

// §21/§22/§23 — version history (with a two-version diff view), traceability links, and (for
// functional requirements) change-impact analysis, all collapsed behind one toggle so
// ArtifactCard doesn't get noisy.
export function ArtifactDetails({ artifactId, artifactType }: { artifactId: string; artifactType: number }) {
  const { token } = useAuth();
  const [open, setOpen] = useState(false);
  const [versions, setVersions] = useState<ArtifactVersion[] | null>(null);
  const [relationships, setRelationships] = useState<ArtifactRelationship[] | null>(null);
  const [impact, setImpact] = useState<ImpactAnalysisResult | null>(null);
  const [loadingImpact, setLoadingImpact] = useState(false);
  const [compareFrom, setCompareFrom] = useState<number | null>(null);
  const [compareTo, setCompareTo] = useState<number | null>(null);

  useEffect(() => {
    if (!open) return;
    artifactsApi.getVersions(artifactId, token).then((v) => {
      setVersions(v);
      if (v.length > 1) {
        setCompareFrom(v[1].versionNumber);
        setCompareTo(v[0].versionNumber);
      }
    }).catch(() => setVersions([]));
    artifactsApi.getRelationships(artifactId, token).then(setRelationships).catch(() => setRelationships([]));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, artifactId]);

  const fromVersion = versions?.find((v) => v.versionNumber === compareFrom);
  const toVersion = versions?.find((v) => v.versionNumber === compareTo);
  const diffRows = fromVersion && toVersion ? diffVersions(fromVersion.data, toVersion.data) : null;

  const checkImpact = async () => {
    setLoadingImpact(true);
    try {
      setImpact(await traceabilityApi.getImpact(artifactId, token));
    } finally {
      setLoadingImpact(false);
    }
  };

  return (
    <div className="artifact-details">
      <button type="button" className="link-button details-toggle" onClick={() => setOpen((o) => !o)}>
        {open ? 'Hide history & links' : 'History & links'}
      </button>

      {open && (
        <div className="details-body">
          {versions && versions.length > 1 && (
            <div className="details-section">
              <h4>Version history</h4>
              <ul className="version-list">
                {versions.map((v) => (
                  <li key={v.versionNumber}>
                    <span className="code">v{v.versionNumber}</span>
                    <span className={`origin-pill origin-${v.origin === 0 ? 'ai' : 'human'}`}>{v.origin === 0 ? 'AI' : 'Human'}</span>
                    {v.reason && <span className="hint"> — {v.reason}</span>}
                    <span className="hint"> · {new Date(v.changedAt).toLocaleString()}</span>
                  </li>
                ))}
              </ul>

              <div className="version-diff-controls">
                <span className="hint">Compare</span>
                <select value={compareFrom ?? ''} onChange={(e) => setCompareFrom(Number(e.target.value))}>
                  {versions.map((v) => (
                    <option key={v.versionNumber} value={v.versionNumber}>v{v.versionNumber}</option>
                  ))}
                </select>
                <span className="hint">to</span>
                <select value={compareTo ?? ''} onChange={(e) => setCompareTo(Number(e.target.value))}>
                  {versions.map((v) => (
                    <option key={v.versionNumber} value={v.versionNumber}>v{v.versionNumber}</option>
                  ))}
                </select>
              </div>

              {diffRows && (
                <table className="version-diff-table">
                  <thead>
                    <tr>
                      <th>Field</th>
                      <th>v{compareFrom}</th>
                      <th>v{compareTo}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {diffRows.map((row) => (
                      <tr key={row.key} className={row.changed ? 'diff-changed' : undefined}>
                        <td>{row.key}</td>
                        <td>{row.oldValue}</td>
                        <td>{row.newValue}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>
          )}

          {relationships && relationships.length > 0 && (
            <div className="details-section">
              <h4>Related artifacts</h4>
              <ul className="relationship-list">
                {relationships.map((r, i) => (
                  <li key={i}>
                    <span className="code">{r.relatedArtifactCode}</span> {r.relatedArtifactTitle}
                    <span className="hint">
                      {' '}
                      ({r.isOutgoing ? 'this' : r.relatedArtifactCode} {RELATIONSHIP_TYPE_LABELS[r.relationshipType]} {r.isOutgoing ? r.relatedArtifactCode : 'this'})
                    </span>
                  </li>
                ))}
              </ul>
            </div>
          )}

          {artifactType === ARTIFACT_TYPE_VALUES.FunctionalRequirement && (
            <div className="details-section">
              <h4>Change impact (§22)</h4>
              <button type="button" disabled={loadingImpact} onClick={checkImpact}>
                {loadingImpact ? 'Checking…' : 'Check impact of changing this'}
              </button>
              {impact && (
                <>
                  {impact.approvedOrLaterDownstream.length === 0 && impact.otherDownstream.length === 0 ? (
                    <p className="hint">No downstream artifacts found.</p>
                  ) : (
                    <>
                      {impact.approvedOrLaterDownstream.length > 0 && (
                        <>
                          <p className="impact-warning">Already approved/implemented — changing this needs review:</p>
                          <ul className="impact-list">
                            {impact.approvedOrLaterDownstream.map((a) => (
                              <li key={a.id}>
                                <span className="code">{a.code}</span> {a.title} <span className="hint">({a.artifactType}, {a.status})</span>
                              </li>
                            ))}
                          </ul>
                        </>
                      )}
                      {impact.otherDownstream.length > 0 && (
                        <>
                          <p className="hint">Not yet approved:</p>
                          <ul className="impact-list">
                            {impact.otherDownstream.map((a) => (
                              <li key={a.id}>
                                <span className="code">{a.code}</span> {a.title} <span className="hint">({a.artifactType}, {a.status})</span>
                              </li>
                            ))}
                          </ul>
                        </>
                      )}
                    </>
                  )}
                </>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
