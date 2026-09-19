import { useState } from 'react';
import type { ArtifactSummary, ClarificationQuestionData } from '../api/types';

interface Props {
  artifact: ArtifactSummary;
  canAnswer: boolean;
  onAnswer: (artifactId: string, answer: string, notApplicable: boolean) => Promise<void>;
}

export function ClarificationQuestionCard({ artifact, canAnswer, onAnswer }: Props) {
  const data = artifact.data as unknown as ClarificationQuestionData;
  const [answer, setAnswer] = useState(data.answer ?? '');
  const [submitting, setSubmitting] = useState(false);

  const isOpen = data.clarificationStatus === 'Open';

  const submit = async (notApplicable: boolean) => {
    setSubmitting(true);
    try {
      await onAnswer(artifact.id, answer, notApplicable);
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <li className={`cq-card cq-${data.clarificationStatus.toLowerCase()}`}>
      <div className="cq-header">
        <span className="code">{artifact.code}</span>
        <span className={`cq-status cq-status-${data.clarificationStatus.toLowerCase()}`}>{data.clarificationStatus}</span>
      </div>
      <p className="cq-question">{data.question}</p>
      {data.reason && <p className="hint">Why it matters: {data.reason}</p>}

      {isOpen && canAnswer ? (
        <div className="cq-answer-form">
          <textarea
            value={answer}
            onChange={(e) => setAnswer(e.target.value)}
            rows={2}
            placeholder="Type the answer…"
          />
          <div className="cq-answer-actions">
            <button type="button" disabled={submitting || !answer.trim()} onClick={() => submit(false)}>
              Submit answer
            </button>
            <button type="button" className="secondary" disabled={submitting} onClick={() => submit(true)}>
              Not applicable
            </button>
          </div>
        </div>
      ) : (
        data.answer && (
          <p className="cq-answer">
            <strong>Answer:</strong> {data.answer}
          </p>
        )
      )}
    </li>
  );
}
