import { useEffect, useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { artifactsApi } from '../api/artifacts';
import { REVIEW_DECISION, type ArtifactReview, type ArtifactSummary } from '../api/types';
import { personLabel, useMemberNames } from '../hooks/useMemberNames';
import { apiErrorText } from '../lib/errors';
import { formatRelativeTime } from '../lib/format';

/**
 * A Developer's comment-only review of a design artifact (§17): the Design exit check needs one on
 * the current version of every solution design, API specification and data entity. It neither
 * approves nor rejects the artifact, and editing the artifact (a new version) needs a new review.
 * Recorded through the existing POST /api/artifacts/{id}/reviews; the backend decides what counts.
 */
export function DesignReviewPanel({ artifact, onRecorded }: { artifact: ArtifactSummary; onRecorded?: () => void }) {
  const { token, hasRole, userId } = useAuth();
  const names = useMemberNames(artifact.projectId);
  const [reviews, setReviews] = useState<ArtifactReview[] | null>(null);
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [version, setVersion] = useState(0);

  useEffect(() => {
    let cancelled = false;
    artifactsApi
      .getReviews(artifact.id, token)
      .then((r) => !cancelled && setReviews(r))
      .catch(() => !cancelled && setReviews([]));
    return () => {
      cancelled = true;
    };
  }, [artifact.id, artifact.currentVersion, token, version]);

  const record = async () => {
    setBusy(true);
    setError(null);
    try {
      await artifactsApi.addCommentReview(artifact.id, comment.trim(), token);
      setComment('');
      setVersion((v) => v + 1);
      onRecorded?.();
    } catch (err) {
      setError(apiErrorText(err, 'The review could not be recorded.'));
    } finally {
      setBusy(false);
    }
  };

  const devReviews = (reviews ?? []).filter((r) => r.decision === REVIEW_DECISION.CommentOnly && r.reviewerRole === 'Developer');
  const current = devReviews.find((r) => r.versionNumber === artifact.currentVersion);
  // Only a review recorded under the Developer role counts for the Design exit check.
  const mayReview = hasRole('Developer');

  return (
    <div className="work-record" aria-label="Developer design review">
      <div className="work-record-head">
        <strong>Developer review</strong>
        {reviews === null ? (
          <span className="muted">Loading…</span>
        ) : current ? (
          <span className="status-pill tone-success">Reviewed v{artifact.currentVersion}</span>
        ) : (
          <span className="status-pill tone-neutral">Not reviewed (v{artifact.currentVersion})</span>
        )}
      </div>
      <p className="hint">
        A Developer reviews the current version of each design artifact before the Design stage can be submitted. A review is a comment only: it does not approve
        or reject the artifact, and an edit creates a new version that needs a new review.
      </p>
      {mayReview && !current && (
        <div className="work-record-actions">
          <input type="text" placeholder="Review comment (required)" value={comment} onChange={(e) => setComment(e.target.value)} disabled={busy} aria-label="Design review comment" />
          <button type="button" className="btn btn-primary btn-sm" disabled={busy || !comment.trim()} onClick={record}>
            {busy ? 'Saving…' : 'Record review'}
          </button>
        </div>
      )}
      {error && <p className="field-error-text">{error}</p>}
      {devReviews.length > 0 && (
        <details className="work-record-history">
          <summary>Developer reviews ({devReviews.length})</summary>
          <ul>
            {devReviews.map((r) => (
              <li key={r.id}>
                <span className="status-pill tone-info">v{r.versionNumber ?? '?'}</span>
                <span>{personLabel(r.reviewerUserId, r.reviewerRole, names, userId)}</span>
                <span className="muted">{formatRelativeTime(r.reviewedAt)}</span>
                {r.comment && <span className="muted">“{r.comment}”</span>}
              </li>
            ))}
          </ul>
        </details>
      )}
    </div>
  );
}
