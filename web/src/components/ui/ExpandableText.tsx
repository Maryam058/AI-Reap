import { useLayoutEffect, useRef, useState, type ReactNode } from 'react';

/**
 * Long prose clamped to a few lines, with a Show more / Show less toggle that appears only when
 * the text actually overflows. The full text is always in the DOM, so nothing is hidden from
 * assistive technology or search.
 */
export function ExpandableText({ children, lines = 3, className }: { children: ReactNode; lines?: number; className?: string }) {
  const ref = useRef<HTMLSpanElement>(null);
  const [expanded, setExpanded] = useState(false);
  const [overflows, setOverflows] = useState(false);

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el || expanded) return;
    const measure = () => setOverflows(el.scrollHeight - el.clientHeight > 1);
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, [children, expanded, lines]);

  return (
    <span className={`expandable-text${className ? ` ${className}` : ''}`}>
      <span ref={ref} className={`expandable-text-body${expanded ? '' : ' clamped'}`} style={{ WebkitLineClamp: expanded ? undefined : lines }}>
        {children}
      </span>
      {(overflows || expanded) && (
        <button type="button" className="expandable-text-toggle" aria-expanded={expanded} onClick={() => setExpanded((v) => !v)}>
          {expanded ? 'Show less' : 'Show more'}
        </button>
      )}
    </span>
  );
}
