import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useAuth } from '../auth/AuthContext';
import { copilotApi } from '../api/copilot';
import type { CopilotAnswer } from '../api/types';
import { copilotContextNotice, DEFAULT_MAX_QUESTION_CHARS, effectiveMaxQuestionChars, questionLengthError } from '../lib/copilotContext';
import { apiErrorText } from '../lib/errors';
import { IconAlert, IconClock, IconDocument, IconSearch, IconShieldCheck, IconSparkles } from './icons';

// Free-form questions the existing copilot endpoint already answers (gaps, coverage, recent changes).
const SUGGESTIONS = [
  "What's still unresolved?",
  'Which requirements lack test cases?',
  'What changed recently?',
  'Which requirements are not yet approved?',
];

// Guidance shown before the first question. Each area maps onto context the copilot is actually given
// (open clarifications, incomplete and untested requirements, recent changes and change impacts,
// topic-relevant requirement content and uploaded documents).
const GUIDE: { title: string; icon: ReactNode; text: string; prompts: string[] }[] = [
  {
    title: 'Gaps & completeness',
    icon: <IconSearch width={16} height={16} />,
    text: 'Find requirements that are missing detail or still waiting on an answer.',
    prompts: ['Which functional requirements are incomplete, and what is missing?', 'Which clarification questions are still open?'],
  },
  {
    title: 'Test coverage',
    icon: <IconShieldCheck width={16} height={16} />,
    text: 'See which requirements are not yet covered by test cases.',
    prompts: ['Which functional requirements have no test cases yet?', 'Summarise test coverage for this project.'],
  },
  {
    title: 'Changes & impact',
    icon: <IconClock width={16} height={16} />,
    text: 'Catch up on recent edits and what they may affect downstream.',
    prompts: ['Which artifacts changed most recently?', 'Are there change impacts that still need review?'],
  },
  {
    title: 'Requirement content',
    icon: <IconDocument width={16} height={16} />,
    text: 'Ask about a feature, user role or rule in plain language.',
    prompts: ['Summarise the requirements related to user login.', 'What do the uploaded documents say about data retention?'],
  },
];

/** What the copilot can answer, with example prompts that fill the question box (they never send it). */
function CopilotGuide({ onUse, disabled }: { onUse: (prompt: string) => void; disabled: boolean }) {
  return (
    <section className="copilot-guide" aria-labelledby="copilot-guide-title">
      <div className="copilot-guide-head">
        <h3 id="copilot-guide-title">What you can ask</h3>
        <p>
          Copilot answers from this project's requirements, clarification questions, test coverage, recent changes and uploaded documents. Pick an
          example to put it in the question box, edit it if you like, then press Ask.
        </p>
      </div>
      <div className="copilot-guide-grid">
        {GUIDE.map((g) => (
          <div key={g.title} className="copilot-guide-card">
            <div className="copilot-guide-card-head">
              <span className="copilot-guide-icon" aria-hidden="true">
                {g.icon}
              </span>
              <h4>{g.title}</h4>
            </div>
            <p>{g.text}</p>
            <ul>
              {g.prompts.map((p) => (
                <li key={p}>
                  <button type="button" className="copilot-guide-prompt" disabled={disabled} onClick={() => onUse(p)} title="Use this question">
                    {p}
                  </button>
                </li>
              ))}
            </ul>
          </div>
        ))}
      </div>
      <ul className="copilot-guide-tips">
        <li>
          <strong>Be specific.</strong> Naming a feature, user role or topic gives more focused answers.
        </li>
        <li>
          <strong>Check the sources.</strong> Answers list related artifacts and cite document passages — verify them before acting.
        </li>
        <li>
          <strong>Read-only.</strong> Copilot never edits requirements or moves the lifecycle; each question is recorded in the AI audit trail.
        </li>
      </ul>
    </section>
  );
}

export function CopilotPanel({ projectId, contextLabel }: { projectId: string; contextLabel?: string }) {
  const { token } = useAuth();
  const [question, setQuestion] = useState('');
  const [answer, setAnswer] = useState<{ q: string; a: CopilotAnswer } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [maxChars, setMaxChars] = useState(DEFAULT_MAX_QUESTION_CHARS);
  const inputRef = useRef<HTMLInputElement>(null);

  // The server's configured limit; the default stands if it cannot be read (the server enforces it anyway).
  useEffect(() => {
    let active = true;
    copilotApi
      .limits(projectId, token)
      .then((limits) => {
        if (active) setMaxChars(effectiveMaxQuestionChars(limits.maxQuestionChars));
      })
      .catch(() => undefined);
    return () => {
      active = false;
    };
  }, [projectId, token]);

  const lengthError = questionLengthError(question, maxChars);
  const notice = answer ? copilotContextNotice(answer.a.context) : null;

  const ask = async (text: string) => {
    if (!text.trim() || busy || questionLengthError(text, maxChars)) return;
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
        <input
          ref={inputRef}
          id="copilot-q"
          type="text"
          placeholder="Ask about gaps, coverage or recent changes…"
          value={question}
          aria-invalid={lengthError ? true : undefined}
          aria-describedby={lengthError ? 'copilot-q-length' : undefined}
          onChange={(e) => setQuestion(e.target.value)}
        />
        <button type="submit" className="btn btn-primary" disabled={!question.trim() || busy || !!lengthError}>
          <IconSparkles width={15} height={15} />
          {busy ? 'Thinking…' : 'Ask'}
        </button>
      </form>

      {lengthError && (
        <div id="copilot-q-length" className="alert alert-warning" role="alert">
          <IconAlert width={16} height={16} />
          <span>{lengthError}</span>
        </div>
      )}

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

      {!answer && !busy && (
        <CopilotGuide
          disabled={busy}
          onUse={(p) => {
            setQuestion(p);
            inputRef.current?.focus();
          }}
        />
      )}

      {busy && <div className="skeleton skeleton-panel" aria-label="Waiting for an answer" />}

      {answer && !busy && (
        <article className="copilot-answer" aria-live="polite">
          <div className="copilot-q">{answer.q}</div>
          <p>{answer.a.answer}</p>

          {notice && (
            <div className="alert alert-info" role="note">
              <span>{notice}</span>
            </div>
          )}

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
