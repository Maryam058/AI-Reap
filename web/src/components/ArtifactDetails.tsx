import { useEffect, useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { artifactsApi } from '../api/artifacts';
import { traceabilityApi } from '../api/traceability';
import { apiErrorText } from '../lib/errors';
import {
  ARTIFACT_PRIORITY_LABELS,
  ARTIFACT_STATUS,
  ARTIFACT_STATUS_LABELS,
  RELATIONSHIP_TYPE_LABELS,
  type ArtifactRelationship,
  type ArtifactReview,
  type ArtifactVersion,
  type ImpactAnalysisResult,
  type ImpactNotice,
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

// §23 — field-by-field diff between two version snapshots: title, priority and status (when the
// snapshot has them) plus the data payload. Entirely client-side: the versions list already
// carries each version's full snapshot, so no dedicated diff endpoint is needed.
function diffVersions(a: ArtifactVersion, b: ArtifactVersion): DiffRow[] {
  const header: Array<[string, unknown, unknown]> = [
    ['title', a.title, b.title],
    ['priority', a.priority != null ? ARTIFACT_PRIORITY_LABELS[a.priority] : null, b.priority != null ? ARTIFACT_PRIORITY_LABELS[b.priority] : null],
    ['status', a.status != null ? ARTIFACT_STATUS_LABELS[a.status] : null, b.status != null ? ARTIFACT_STATUS_LABELS[b.status] : null],
  ];
  const keys = Array.from(new Set([...Object.keys(a.data), ...Object.keys(b.data)])).sort();
  return [
    ...header.filter(([, x, y]) => x != null || y != null).map(([key, x, y]) => [key, x, y] as const),
    ...keys.map((key) => [key, a.data[key], b.data[key]] as const),
  ].map(([key, x, y]) => {
    const oldValue = toDisplayValue(x);
    const newValue = toDisplayValue(y);
    return { key, oldValue, newValue, changed: oldValue !== newValue };
  });
}

interface Props {
  artifactId: string;
  artifactType: number;
  // §22 — open "upstream changed" notices; when there are any the panel opens straight to them.
  openImpactNoticeCount?: number;
  onNoticeAcknowledged?: () => void;
}

// §21/§22/§23/§24 — impact notices, version history (with a two-version diff view), review
// history, traceability links and change-impact analysis, collapsed behind one toggle so
// ArtifactCard doesn't get noisy.
export function ArtifactDetails({ artifactId, openImpactNoticeCount = 0, onNoticeAcknowledged }: Props) {
  const { token } = useAuth();
  const [open, setOpen] = useState(false);
  const [versions, setVersions] = useState<ArtifactVersion[] | null>(null);
  const [reviews, setReviews] = useState<ArtifactReview[] | null>(null);
  const [notices, setNotices] = useState<ImpactNotice[] | null>(null);
  const [relationships, setRelationships] = useState<ArtifactRelationship[] | null>(null);
  const [impact, setImpact] = useState<ImpactAnalysisResult | null>(null);
  const [loadingImpact, setLoadingImpact] = useState(false);
  const [impactError, setImpactError] = useState<string | null>(null);
  const [compareFrom, setCompareFrom] = useState<number | null>(null);
  const [compareTo, setCompareTo] = useState<number | null>(null);
  const [ackNote, setAckNote] = useState('');
  const [ackBusy, setAckBusy] = useState<string | null>(null);
  const [ackError, setAckError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    artifactsApi.getVersions(artifactId, token).then((v) => {
      setVersions(v);
      if (v.length > 1) {
        setCompareFrom(v[1].versionNumber);
        setCompareTo(v[0].versionNumber);
      }
    }).catch(() => setVersions([]));
    artifactsApi.getReviews(artifactId, token).then(setReviews).catch(() => setReviews([]));
    traceabilityApi.getArtifactImpactNotices(artifactId, token).then(setNotices).catch(() => setNotices([]));
    artifactsApi.getRelationships(artifactId, token).then(setRelationships).catch(() => setRelationships([]));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, artifactId, openImpactNoticeCount]);

  const fromVersion = versions?.find((v) => v.versionNumber === compareFrom);
  const toVersion = versions?.find((v) => v.versionNumber === compareTo);
  const diffRows = fromVersion && toVersion ? diffVersions(fromVersion, toVersion) : null;
  const openNotices = notices?.filter((n) => !n.acknowledgedAt) ?? [];

  const checkImpact = async () => {
    setLoadingImpact(true);
    setImpactError(null);
    try {
      setImpact(await traceabilityApi.getImpact(artifactId, token));
    } catch (err) {
      setImpactError(apiErrorText(err, 'Could not run the impact analysis.'));
    } finally {
      setLoadingImpact(false);
    }
  };

  const acknowledge = async (noticeId: string) => {
    setAckBusy(noticeId);
    setAckError(null);
    try {
      const updated = await traceabilityApi.acknowledgeImpactNotice(noticeId, ackNote.trim() || undefined, token);
      setNotices((current) => current?.map((n) => (n.id === updated.id ? updated : n)) ?? null);
      setAckNote('');
      onNoticeAcknowledged?.();
    } catch (err) {
      setAckError(apiErrorText(err, 'Could not acknowledge the notice.'));
    } finally {
      setAckBusy(null);
    }
  };

  const renderImpactList = (items: ImpactAnalysisResult['otherDownstream']) => (
    <ul className="impact-list">
      {items.map((a) => (
        <li key={a.id}>
          <span className="code">{a.code}</span> {a.title} <span className="hint">({a.artifactType}, {a.status})</span>
          {a.path && <div className="hint">{a.path}</div>}
        </li>
      ))}
    </ul>
  );

  return (
    <div className="artifact-details">
      <button type="button" className="link-button details-toggle" onClick={() => setOpen((o) => !o)}>
        {open ? 'Hide history & links' : openImpactNoticeCount > 0 ? `Review upstream change (${openImpactNoticeCount})` : 'History & links'}
      </button>

      {open && (
        <div className="details-body">
          {openNotices.length > 0 && (
            <div className="details-section">
              <h4>Upstream changes to review (§22)</h4>
              <p className="impact-warning">
                An artifact this one depends on was changed after approval. This artifact was not modified; review it
                against the change, edit it if needed, then acknowledge.
              </p>
              <ul className="impact-list">
                {openNotices.map((n) => (
                  <li key={n.id}>
                    <span className="code">{n.sourceArtifactCode}</span> changed to v{n.sourceVersion}: {n.sourceArtifactTitle}
                    <div className="hint">{n.path} · {new Date(n.createdAt).toLocaleString()}</div>
                    <button type="button" className="btn btn-secondary btn-sm" disabled={ackBusy !== null} onClick={() => acknowledge(n.id)}>
                      {ackBusy === n.id ? 'Acknowledging…' : 'Acknowledge'}
                    </button>
                  </li>
                ))}
              </ul>
              <input
                placeholder="Optional note, e.g. 'Checked - still valid'"
                value={ackNote}
                maxLength={2000}
                onChange={(e) => setAckNote(e.target.value)}
              />
              {ackError && <p className="error">{ackError}</p>}
            </div>
          )}

          {versions && versions.length > 1 && (
            <div className="details-section">
              <h4>Version history</h4>
              <ul className="version-list">
                {versions.map((v) => (
                  <li key={v.versionNumber}>
                    <span className="code">v{v.versionNumber}</span>
                    <span className={`origin-pill origin-${v.origin === 0 ? 'ai' : 'human'}`}>{v.origin === 0 ? 'AI' : 'Human'}</span>
                    {v.status != null && (
                      <span className={`status-pill tone-${v.status === ARTIFACT_STATUS.Approved ? 'success' : 'neutral'}`}>
                        {ARTIFACT_STATUS_LABELS[v.status]}
                      </span>
                    )}
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

          {reviews && reviews.length > 0 && (
            <div className="details-section">
              <h4>Review decisions</h4>
              <ul className="version-list">
                {reviews.map((r) => (
                  <li key={r.id}>
                    <span className={`status-pill tone-${r.decision === 0 ? 'success' : r.decision === 1 ? 'danger' : 'neutral'}`}>
                      {r.decision === 0 ? 'Approved' : r.decision === 1 ? 'Rejected' : 'Comment'}
                    </span>
                    {r.versionNumber != null && <span className="code"> v{r.versionNumber}</span>}
                    {r.comment && <span className="hint"> — {r.comment}</span>}
                    <span className="hint"> · {new Date(r.reviewedAt).toLocaleString()}</span>
                  </li>
                ))}
              </ul>
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

          <div className="details-section">
            <h4>Change impact (§22)</h4>
            <button type="button" disabled={loadingImpact} onClick={checkImpact}>
              {loadingImpact ? 'Checking…' : 'Check impact of changing this'}
            </button>
            {impactError && <p className="error">{impactError}</p>}
            {impact && (
              <>
                {impact.approvedOrLaterDownstream.length === 0 && impact.otherDownstream.length === 0 ? (
                  <p className="hint">No downstream artifacts found.</p>
                ) : (
                  <>
                    {impact.approvedOrLaterDownstream.length > 0 && (
                      <>
                        <p className="impact-warning">Already approved/implemented — changing this needs review:</p>
                        {renderImpactList(impact.approvedOrLaterDownstream)}
                      </>
                    )}
                    {impact.otherDownstream.length > 0 && (
                      <>
                        <p className="hint">Not yet approved:</p>
                        {renderImpactList(impact.otherDownstream)}
                      </>
                    )}
                  </>
                )}
              </>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
