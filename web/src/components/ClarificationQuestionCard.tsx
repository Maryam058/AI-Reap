import { useState } from 'react';
import type { ArtifactSummary, ClarificationQuestionData, SourceReference } from '../api/types';
import { evidencePurpose, isLocatedEvidence, sourceLinkAriaLabel, sourceLinkLabel } from '../lib/sourceReferences';
import { IconDocument } from './icons';
import { SourceDocumentViewer } from './SourceDocumentViewer';

const KIND_LABELS: Record<string, string> = {
  missing: 'Missing information',
  ambiguous: 'Ambiguity',
  contradiction: 'Contradiction',
  'undefined-rule': 'Undefined rule',
};

interface Props {
  artifact: ArtifactSummary;
  canAnswer: boolean;
  onAnswer: (artifactId: string, answer: string, notApplicable: boolean) => Promise<void>;
}

// A clarification question: the question, and a link to the verified passage of the requirement source
// that raised it (both statements, for a conflict). The server only returns questions whose passage it
// verified (ClarificationVisibility), so there is no "unlinked" state to show; a caller who may not read
// raw sources (S2) gets no passages, and so no link.
export function ClarificationQuestionCard({ artifact, canAnswer, onAnswer }: Props) {
  const data = artifact.data as unknown as ClarificationQuestionData;
  const [answer, setAnswer] = useState(data.answer ?? '');
  const [submitting, setSubmitting] = useState(false);
  // The passage the viewer opens, with the question's other passages to step through.
  const [opened, setOpened] = useState<SourceReference | null>(null);

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
  const passages = (data.sourceEvidence ?? []).filter(isLocatedEvidence);
  const conflict = data.kind === 'contradiction' && passages.length > 1;

  return (
    <li className={`cq-card cq-${status}`}>
      <div className="cq-header">
        <span className="code">{artifact.code}</span>
        <span className={`status-pill tone-${statusTone}`}>{statusLabel}</span>
        {data.kind && <span className="cq-kind">{KIND_LABELS[data.kind] ?? data.kind}</span>}
      </div>
      <p className="cq-question">{data.question}</p>
      {passages.length > 0 && (
        <div className="cq-sources" role="group" aria-label="Source in the requirement document">
          {passages.map((p, i) => {
            const label = sourceLinkLabel(p);
            const statement = conflict ? `Statement ${i + 1}` : null;
            return (
              <button
                key={i}
                type="button"
                className="cq-source"
                aria-haspopup="dialog"
                aria-label={sourceLinkAriaLabel(p, statement)}
                title={p.excerpt ?? undefined}
                onClick={() => setOpened(p)}
              >
                <IconDocument width={14} height={14} aria-hidden="true" />
                {statement && <span className="cq-source-statement">{statement}</span>}
                <span className="cq-source-label">{label}</span>
              </button>
            );
          })}
        </div>
      )}
      {opened && (
        <SourceDocumentViewer
          reference={opened}
          passages={passages.length > 1 ? passages : undefined}
          purpose={evidencePurpose(data.kind)}
          question={data.question}
          onClose={() => setOpened(null)}
        />
      )}
      {data.relatedDocuments && data.relatedDocuments.length > 0 && (
        <p className="cq-related">
          <span className="detail-label">Project documents that may bear on this:</span>{' '}
          {data.relatedDocuments.map((d) => d.reference).join(', ')}
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
