import { useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { copilotApi } from '../api/copilot';
import type { CopilotAnswer } from '../api/types';
import { apiErrorText } from '../lib/errors';
import { IconAlert, IconSparkles } from './icons';

// Free-form questions the existing copilot endpoint already answers (gaps, coverage, recent changes).
const SUGGESTIONS = [
  "What's still unresolved?",
  'Which requirements lack test cases?',
  'What changed recently?',
  'Which requirements are not yet approved?',
];

export function CopilotPanel({ projectId, contextLabel }: { projectId: string; contextLabel?: string }) {
  const { token } = useAuth();
  const [question, setQuestion] = useState('');
  const [answer, setAnswer] = useState<{ q: string; a: CopilotAnswer } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const ask = async (text: string) => {
    if (!text.trim() || busy) return;
    setBusy(true);
    setError(null);
    try {
      setAnswer({ q: text, a: await copilotApi.ask(projectId, text, token) });
    } catch (err) {
      setError(apiErrorText(err, 'The copilot could not answer right now. Please try again.'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="copilot">
      <div className="copilot-context">
        <span className="ai-badge">
          <IconSparkles width={12} height={12} /> Grounded in this project
        </span>
        {contextLabel && <span className="copilot-chip">Context: {contextLabel}</span>}
      </div>

      <form
        className="copilot-ask"
        onSubmit={(e) => {
          e.preventDefault();
          void ask(question);
        }}
      >
        <label className="sr-only" htmlFor="copilot-q">
          Ask the project copilot
        </label>
        <input id="copilot-q" type="text" placeholder="Ask about gaps, coverage or recent changes…" value={question} onChange={(e) => setQuestion(e.target.value)} />
        <button type="submit" className="btn btn-primary" disabled={!question.trim() || busy}>
          <IconSparkles width={15} height={15} />
          {busy ? 'Thinking…' : 'Ask'}
        </button>
      </form>

      <div className="copilot-suggestions" aria-label="Suggested questions">
        {SUGGESTIONS.map((s) => (
          <button
            key={s}
            type="button"
            className="copilot-suggestion"
            disabled={busy}
            onClick={() => {
              setQuestion(s);
              void ask(s);
            }}
          >
            {s}
          </button>
        ))}
      </div>

      {error && (
        <div className="alert alert-danger" role="alert">
          <IconAlert width={16} height={16} />
          <span>{error}</span>
        </div>
      )}

      {busy && <div className="skeleton skeleton-panel" aria-label="Waiting for an answer" />}

      {answer && !busy && (
        <article className="copilot-answer" aria-live="polite">
          <div className="copilot-q">{answer.q}</div>
          <p>{answer.a.answer}</p>

          {answer.a.relatedArtifactCodes.length > 0 && (
            <div className="copilot-related">
              <span className="detail-label">Related</span>
              {answer.a.relatedArtifactCodes.map((c) => (
                <span key={c} className="code trace-chip">
                  {c}
                </span>
              ))}
            </div>
          )}

          {answer.a.citations.length > 0 && (
            <div className="copilot-citations">
              <span className="detail-label">Sources</span>
              <ul>
                {answer.a.citations.map((c, i) => (
                  <li key={i}>
                    <span className="hint">
                      {c.documentName} #{c.chunkIndex}
                    </span>
                    <p>{c.snippet}</p>
                  </li>
                ))}
              </ul>
            </div>
          )}
          <p className="copilot-disclaimer">AI-generated — verify against the source requirements.</p>
        </article>
      )}
    </div>
  );
}
