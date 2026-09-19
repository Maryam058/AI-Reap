import { useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { ApiError } from '../api/client';
import { copilotApi } from '../api/copilot';
import type { CopilotAnswer } from '../api/types';

export function CopilotPanel({ projectId }: { projectId: string }) {
  const { token } = useAuth();
  const [question, setQuestion] = useState('');
  const [answer, setAnswer] = useState<CopilotAnswer | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const onAsk = async () => {
    if (!question.trim()) return;
    setBusy(true);
    setError(null);
    try {
      setAnswer(await copilotApi.ask(projectId, question, token));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to get an answer.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="card">
      <h2>Project Copilot</h2>
      <p className="hint">§25 — ask about gaps, coverage, or recent changes. Grounded in project data and uploaded documents; never a separate knowledge source.</p>

      <div className="upload-row">
        <input
          type="text"
          placeholder="e.g. what's unresolved? what lacks test cases?"
          value={question}
          onChange={(e) => setQuestion(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && onAsk()}
        />
        <button type="button" disabled={!question.trim() || busy} onClick={onAsk}>
          {busy ? 'Asking…' : 'Ask'}
        </button>
      </div>

      {error && <p className="error">{error}</p>}

      {answer && (
        <div className="copilot-answer">
          <p>{answer.answer}</p>

          {answer.relatedArtifactCodes.length > 0 && (
            <p className="hint">Related: {answer.relatedArtifactCodes.join(', ')}</p>
          )}

          {answer.citations.length > 0 && (
            <div className="copilot-citations">
              <span className="hint">Sources:</span>
              <ul>
                {answer.citations.map((c, i) => (
                  <li key={i}>
                    <span className="hint">{c.documentName} #{c.chunkIndex}</span>
                    <p>{c.snippet}</p>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
