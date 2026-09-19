import { useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { ApiError } from '../api/client';
import { projectsApi } from '../api/projects';
import type { ConflictFinding } from '../api/types';

export function ConflictsPanel({ projectId }: { projectId: string }) {
  const { token, hasRole } = useAuth();
  const canRun = hasRole('Administrator') || hasRole('BusinessAnalyst') || hasRole('Reviewer');

  const [findings, setFindings] = useState<ConflictFinding[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!canRun) return null;

  const run = async () => {
    setBusy(true);
    setError(null);
    try {
      setFindings(await projectsApi.detectConflicts(projectId, token));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Conflict detection failed.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="card">
      <h2>Duplicate &amp; Conflict Detection</h2>
      <p className="hint">Scans all functional requirements in the project. Conflicts are never auto-resolved.</p>
      <button type="button" disabled={busy} onClick={run}>
        {busy ? 'Scanning…' : 'Detect conflicts'}
      </button>

      {error && <p className="error">{error}</p>}

      {findings && (
        <>
          {findings.length === 0 ? (
            <p>No potential duplicates or conflicts found.</p>
          ) : (
            <ul className="conflict-findings">
              {findings.map((f, i) => (
                <li key={i}>
                  <span className="conflict-badge">
                    {f.relationshipType === 5 ? 'Potential Duplicate' : 'Potential Conflict'} — Human Resolution Required
                  </span>
                  <p>
                    <span className="code">{f.artifactACode}</span> {f.artifactATitle}
                    {' ↔ '}
                    <span className="code">{f.artifactBCode}</span> {f.artifactBTitle}
                  </p>
                  <p className="hint">{f.reason}</p>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </div>
  );
}
