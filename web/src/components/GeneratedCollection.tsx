import { useId, useState, type ReactNode } from 'react';
import { BulkDisclosureContext, nextStamp, useDisclosure, type DisclosureRequest } from '../lib/disclosure';
import { IconChevronDown } from './icons';

/**
 * Shared presentation for AI-generated collections across the SDLC work areas: a collapsible section
 * (title, count, status summary, expand/collapse all) holding collapsible groups of compact cards.
 * Collapsed content stays mounted (only hidden), so in-progress input inside a card is never lost.
 */
export function CollectionSection({
  id,
  title,
  count,
  summary,
  actions,
  bulkControls = false,
  reveal,
  children,
}: {
  id?: string;
  title: string;
  count: number;
  summary?: string;
  actions?: ReactNode;
  /** Show "Expand all" / "Collapse all" for the groups and cards inside. */
  bulkControls?: boolean;
  reveal?: DisclosureRequest | null;
  children: ReactNode;
}) {
  const { open, toggle } = useDisclosure(true, reveal);
  const [bulk, setBulk] = useState<DisclosureRequest | null>(null);
  const bodyId = useId();

  return (
    <section className="req-section gen-section" id={id}>
      <div className="gen-section-head">
        <h3 className="gen-heading">
          <button type="button" className="gen-toggle" aria-expanded={open} aria-controls={bodyId} onClick={toggle}>
            <IconChevronDown width={16} height={16} className="gen-chevron" />
            <span className="gen-title">{title}</span>
            <span className="req-count">{count}</span>
          </button>
        </h3>
        {summary && <span className="gen-summary">{summary}</span>}
        {(actions || (bulkControls && open)) && (
          <div className="gen-tools">
            {actions}
            {bulkControls && open && (
              <>
                <button type="button" className="btn btn-ghost btn-sm" onClick={() => setBulk({ stamp: nextStamp(), open: true })}>
                  Expand all
                </button>
                <button type="button" className="btn btn-ghost btn-sm" onClick={() => setBulk({ stamp: nextStamp(), open: false })}>
                  Collapse all
                </button>
              </>
            )}
          </div>
        )}
      </div>
      <div id={bodyId} className="gen-section-body" hidden={!open}>
        <BulkDisclosureContext.Provider value={bulk}>{children}</BulkDisclosureContext.Provider>
      </div>
    </section>
  );
}

/** Stack of groups inside a section. */
export function CollectionGroups({ children }: { children: ReactNode }) {
  return <div className="gen-groups">{children}</div>;
}

export function CollectionGroup({
  code,
  eyebrow,
  label,
  count,
  countLabel,
  emptyLabel,
  summary,
  actions,
  defaultOpen = false,
  reveal,
  children,
}: {
  code?: string;
  /** What the grouping is, e.g. "Actor" or "Category". */
  eyebrow?: string;
  label: string;
  count: number;
  /** e.g. "5 test cases". */
  countLabel: string;
  /** Shown instead of the count when the group is empty; an empty group cannot be expanded. */
  emptyLabel?: string;
  summary?: string;
  actions?: ReactNode;
  defaultOpen?: boolean;
  reveal?: DisclosureRequest | null;
  children?: ReactNode;
}) {
  const { open, toggle } = useDisclosure(defaultOpen, reveal);
  const bodyId = useId();
  const empty = count === 0;
  const isOpen = open && !empty;

  const heading = (
    <>
      <IconChevronDown width={15} height={15} className="gen-chevron" />
      {code && <span className="code">{code}</span>}
      <span className="gen-group-label">
        {eyebrow && <span className="gen-eyebrow">{eyebrow}</span>}
        <span className="gen-group-title" title={label}>
          {label}
        </span>
      </span>
      <span className={`gen-group-count ${empty ? 'is-empty' : ''}`}>{empty && emptyLabel ? emptyLabel : countLabel}</span>
    </>
  );

  return (
    <div className={`gen-group ${isOpen ? 'is-open' : ''} ${empty ? 'is-empty' : ''}`}>
      <div className="gen-group-head">
        {empty ? (
          <div className="gen-group-toggle is-static">{heading}</div>
        ) : (
          <button type="button" className="gen-group-toggle" aria-expanded={isOpen} aria-controls={bodyId} onClick={toggle}>
            {heading}
          </button>
        )}
        {summary && !empty && <span className="gen-summary gen-group-summary">{summary}</span>}
        {actions && <div className="gen-group-actions">{actions}</div>}
      </div>
      {!empty && (
        <div id={bodyId} className="gen-group-body" hidden={!isOpen}>
          {children}
        </div>
      )}
    </div>
  );
}

export interface OverviewItem {
  id: string;
  label: string;
  count: number;
}

/** First-level "what was generated" summary: one chip per section, jumping to (and opening) it. */
export function GeneratedOverview({ items, onJump }: { items: OverviewItem[]; onJump: (id: string) => void }) {
  if (items.length < 2) return null;
  return (
    <nav className="gen-overview" aria-label="Generated content">
      <span className="gen-overview-label">Generated</span>
      {items.map((i) => (
        <button key={i.id} type="button" className="gen-chip" onClick={() => onJump(i.id)}>
          {i.label}
          <span className="gen-chip-count">{i.count}</span>
        </button>
      ))}
    </nav>
  );
}
