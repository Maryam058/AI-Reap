import { Fragment, useState } from 'react';
import type { ArtifactSummary, ClarificationQuestionData, SourceReference } from '../api/types';
import {
  evidenceHeading,
  evidencePurpose,
  isLocatedEvidence,
  occurrenceNote,
  reasonSegments,
  recoveryNote,
  referenceAriaLabel,
} from '../lib/sourceReferences';
import { IconDocument, IconInfo } from './icons';
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

export function ClarificationQuestionCard({ artifact, canAnswer, onAnswer }: Props) {
  const data = artifact.data as unknown as ClarificationQuestionData;
  const [answer, setAnswer] = useState(data.answer ?? '');
  const [submitting, setSubmitting] = useState(false);
  // What the viewer opens: one passage (with the question's other passages to step through), or the
  // whole source with nothing highlighted when no passage could be verified.
  const [opened, setOpened] = useState<{ reference: SourceReference; passages?: SourceReference[]; browse?: boolean } | null>(null);

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
  // Questions raised before RequirementAnalysis-v3 have no impact field; their reason was shown as "Why it matters".
  const reasonLabel = data.impact ? 'Issue:' : 'Why it matters:';
  const evidence = data.sourceEvidence ?? [];
  const located = evidence.filter(isLocatedEvidence);
  const refs = data.requirementRefs ?? [];
  const reasonLinks = data.reason ? reasonSegments(data.reason, data.sourceReferences).filter((s) => s.reference) : [];
  const openPassage = (e: SourceReference) => setOpened({ reference: e, passages: located.length > 1 ? located : undefined });
  // "Why it matters" is the impact (v3) or, on older questions, the reason. When it names no place
  // itself, it links to the passage that raised the question, so the two always point at one place.
  const whyLinksItself = !data.impact && reasonLinks.length > 0;
  const primary = located[0];
  const unresolved = data.sourceLinkStatus === 'unresolved' && !!artifact.requirementSourceId;

  return (
    <li className={`cq-card cq-${status}`}>
      <div className="cq-header">
        <span className="code">{artifact.code}</span>
        <span className={`status-pill tone-${statusTone}`}>{statusLabel}</span>
        {data.kind && <span className="cq-kind">{KIND_LABELS[data.kind] ?? data.kind}</span>}
        {refs.length > 0 && <span className="cq-refs">Concerns {refs.join(' · ')}</span>}
      </div>
      <p className="cq-question">{data.question}</p>
      {evidence.length > 0 && (
        <div className="cq-evidence-block">
          <span className="detail-label">{evidenceHeading(data.kind, evidence.length)}</span>
          <ul className="cq-evidence" aria-label="Evidence from the requirement source">
            {evidence.map((e, i) => {
              const notes = [recoveryNote(e.recovery), isLocatedEvidence(e) ? occurrenceNote(e) : null].filter(Boolean);
              return (
                <li key={i}>
                  <blockquote>
                    {e.excerpt ?? e.label}
                    {isLocatedEvidence(e) ? (
                      <>
                        {' '}
                        <button
                          type="button"
                          className="source-link"
                          aria-haspopup="dialog"
                          aria-label={referenceAriaLabel(e)}
                          title="Open the requirement source at this passage"
                          onClick={() => openPassage(e)}
                        >
                          <IconDocument width={12} height={12} aria-hidden="true" />
                          {e.sectionNumber ? `§${e.sectionNumber}` : `line ${e.line + 1}`}
                        </button>
                      </>
                    ) : (
                      <span className="cq-evidence-note"> (not located in the current source text)</span>
                    )}
                  </blockquote>
                  {notes.length > 0 && <p className="cq-evidence-note">{notes.join(' ')}</p>}
                </li>
              );
            })}
          </ul>
        </div>
      )}
      {unresolved && (
        <p className="cq-source-unresolved" role="note">
          No passage of the requirement source could be verified as the reason for this question, so none is highlighted.{' '}
          <button
            type="button"
            className="source-link"
            aria-haspopup="dialog"
            onClick={() =>
              setOpened({
                browse: true,
                reference: { label: 'the requirement source', start: 0, sourceId: artifact.requirementSourceId!, line: 0, endLine: 0, anchor: '' },
              })
            }
          >
            <IconDocument width={12} height={12} aria-hidden="true" />
            Browse the source
          </button>
        </p>
      )}
      {data.impact && (
        <p className="cq-reason">
          <IconInfo width={13} height={13} />
          <span>
            <span className="detail-label">Why it matters:</span> {data.impact}
            {primary && <SourcePassageLink reference={primary} onOpen={openPassage} />}
          </span>
        </p>
      )}
      {data.reason && (
        <p className="cq-reason">
          <IconInfo width={13} height={13} />
          <span>
            <span className="detail-label">{reasonLabel}</span>{' '}
            {/* Only server-resolved references become links; the rest is plain text (never HTML). */}
            {reasonSegments(data.reason, data.sourceReferences).map((segment, i) =>
              segment.reference ? (
                <button
                  key={i}
                  type="button"
                  className="source-link"
                  aria-haspopup="dialog"
                  aria-label={referenceAriaLabel(segment.reference)}
                  title="Open the requirement source at this section"
                  onClick={() => setOpened({ reference: segment.reference! })}
                >
                  <IconDocument width={12} height={12} aria-hidden="true" />
                  {segment.text}
                </button>
              ) : (
                <Fragment key={i}>{segment.text}</Fragment>
              ),
            )}
            {!data.impact && !whyLinksItself && primary && <SourcePassageLink reference={primary} onOpen={openPassage} />}
          </span>
        </p>
      )}
      {opened && (
        <SourceDocumentViewer
          reference={opened.reference}
          passages={opened.passages}
          purpose={opened.reference.excerptIsQuote ? evidencePurpose(data.kind) : undefined}
          browse={opened.browse}
          onClose={() => setOpened(null)}
        />
      )}
      {data.relatedDocuments && data.relatedDocuments.length > 0 && (
        <p className="cq-reason">
          <IconDocument width={13} height={13} />
          <span>
            <span className="detail-label">Project documents that may bear on this:</span>{' '}
            {data.relatedDocuments.map((d) => d.reference).join(', ')}
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

// The link after a "Why it matters" that does not name its place: the passage that raised the question.
function SourcePassageLink({ reference, onOpen }: { reference: SourceReference; onOpen: (r: SourceReference) => void }) {
  return (
    <>
      {' '}
      <button
        type="button"
        className="source-link"
        aria-haspopup="dialog"
        aria-label={`Source passage: open the requirement source at ${reference.sectionNumber ? `§${reference.sectionNumber}` : `line ${reference.line + 1}`}`}
        title="Open the requirement source at the passage that raised this question"
        onClick={() => onOpen(reference)}
      >
        <IconDocument width={12} height={12} aria-hidden="true" />
        Source passage
      </button>
    </>
  );
}
