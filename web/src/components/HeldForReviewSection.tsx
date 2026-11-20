import { useState } from 'react';
import type { SourceReference } from '../api/types';
import type { HeldItem, HeldPassage } from '../lib/heldForReview';
import { CollectionSection } from './GeneratedCollection';
import { IconAlert, IconCheckCircle, IconDocument } from './icons';
import { SourceDocumentViewer } from './SourceDocumentViewer';
import { EmptyState } from './ui/EmptyState';

// Characters of a passage shown before "Show more".
const PASSAGE_PREVIEW_CHARS = 420;

// Proposed questions the server held back for a person to look at (lib/heldForReview). They are not
// clarification questions: shown read-only, beside but visibly apart from the accepted questions, with
// the question as generated, the passage it cites, the passage that contradicts it and why it was held.
export function HeldForReviewSection({ items }: { items: HeldItem[] }) {
  return (
    <CollectionSection
      id="sec-held-for-review"
      title="Held for review"
      count={items.length}
      summary={items.length > 0 ? 'Needs human review' : 'None held'}
      description="Questions the AI proposed but did not ask, because each assumes something another passage of the document contradicts. They are not clarification questions and cannot be answered here — a reviewer should read both passages and decide whether a neutral question is needed."
    >
      {items.length === 0 ? (
        <EmptyState
          compact
          icon={<IconCheckCircle width={18} height={18} />}
          title="Nothing held for review"
          description="No proposed question was held back in the latest analysis of this source."
        />
      ) : (
        <ul className="cq-list held-list">
          {items.map((item) => (
            <HeldCard key={item.key} item={item} />
          ))}
        </ul>
      )}
    </CollectionSection>
  );
}

function HeldCard({ item }: { item: HeldItem }) {
  const [opened, setOpened] = useState<SourceReference | null>(null);
  const references = [...item.evidence.map((e) => e.reference), item.contradicting.reference].filter((r): r is SourceReference => r !== null);
  const evidence = item.evidence.length > 0 ? item.evidence : [{ text: null, where: 'No passage was cited', reference: null }];

  return (
    <li className="cq-card held-card">
      <div className="cq-header">
        <span className="status-pill tone-warning held-status">
          <IconAlert width={12} height={12} aria-hidden="true" /> Needs human review
        </span>
        <span className="held-tag">Not a clarification question</span>
        {item.kindLabel && <span className="cq-kind">{item.kindLabel}</span>}
      </div>
      {item.topic && <p className="held-topic">{item.topic}</p>}
      <div className="held-question">
        <span className="detail-label">Original question, as generated</span>
        <p className="cq-question">{item.question}</p>
      </div>

      <div className="held-passages">
        <section className="held-passage" aria-label="Supporting evidence from the document">
          <span className="detail-label">Evidence from the document</span>
          {evidence.map((p, i) => (
            <Passage key={i} passage={p} onOpen={setOpened} />
          ))}
        </section>
        <section className="held-passage held-passage-contradicts" aria-label="Passage that contradicts the assumption">
          <span className="detail-label">Passage that contradicts the assumption</span>
          <Passage passage={item.contradicting} onOpen={setOpened} />
        </section>
      </div>

      <p className="held-reason">
        <span className="detail-label">Why it was held</span>
        {item.reason}
      </p>

      {opened && (
        <SourceDocumentViewer
          reference={opened}
          passages={references.length > 1 ? references : undefined}
          question={item.question}
          onClose={() => setOpened(null)}
        />
      )}
    </li>
  );
}

function Passage({ passage, onOpen }: { passage: HeldPassage; onOpen: (r: SourceReference) => void }) {
  const [showAll, setShowAll] = useState(false);
  const words = passage.text;
  const long = words !== null && words.length > PASSAGE_PREVIEW_CHARS;
  const shown = words !== null && long && !showAll ? `${words.slice(0, PASSAGE_PREVIEW_CHARS).trimEnd()}…` : words;
  return (
    <div className="held-quote-block">
      {shown !== null ? (
        <blockquote className="held-quote">
          {shown}
          {long && (
            <>
              {' '}
              <button type="button" className="req-show-more" aria-expanded={showAll} onClick={() => setShowAll((v) => !v)}>
                {showAll ? 'Show less' : 'Show more'}
              </button>
            </>
          )}
        </blockquote>
      ) : (
        <p className="held-quote-missing">The passage text is not available.</p>
      )}
      <div className="held-where">
        {passage.reference ? (
          <button
            type="button"
            className="cq-source"
            aria-haspopup="dialog"
            aria-label={`Open the requirement source at ${passage.where}`}
            onClick={() => onOpen(passage.reference!)}
          >
            <IconDocument width={14} height={14} aria-hidden="true" />
            <span className="cq-source-label">{passage.where}</span>
          </button>
        ) : (
          <span className="held-where-text">{passage.where}</span>
        )}
      </div>
    </div>
  );
}
