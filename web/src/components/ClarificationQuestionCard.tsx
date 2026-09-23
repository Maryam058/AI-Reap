import { useState } from 'react';
import type { ArtifactSummary, ClarificationQuestionData } from '../api/types';
import { IconInfo } from './icons';

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

  const status = data.clarificationStatus.toLowerCase();
  const statusTone = isOpen ? 'warning' : status === 'notapplicable' ? 'neutral' : 'success';
  const statusLabel = status === 'notapplicable' ? 'Not applicable' : data.clarificationStatus;

  return (
    <li className={`cq-card cq-${status}`}>
      <div className="cq-header">
        <span className="code">{artifact.code}</span>
        <span className={`status-pill tone-${statusTone}`}>{statusLabel}</span>
      </div>
      <p className="cq-question">{data.question}</p>
      {data.reason && (
        <p className="cq-reason">
          <IconInfo width={13} height={13} />
          <span>
            <span className="detail-label">Why it matters:</span> {data.reason}
          </span>
        </p>
      )}

      {isOpen && canAnswer ? (
        <div className="cq-answer-form">
          <textarea
            className="req-textarea"
            value={answer}
            onChange={(e) => setAnswer(e.target.value)}
            rows={2}
            placeholder="Type the answer…"
            aria-label={`Answer for ${artifact.code}`}
          />
          <div className="cq-answer-actions">
            <button type="button" className="btn btn-primary btn-sm" disabled={submitting || !answer.trim()} onClick={() => submit(false)}>
              Submit answer
            </button>
            <button type="button" className="btn btn-ghost btn-sm" disabled={submitting} onClick={() => submit(true)}>
              Not applicable
            </button>
          </div>
        </div>
      ) : (
        data.answer && (
          <p className="cq-answer">
            <span className="detail-label">Answer</span>
            {data.answer}
          </p>
        )
      )}
    </li>
  );
}
