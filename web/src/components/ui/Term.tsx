import { useId, useState, type CSSProperties } from 'react';
import { glossary } from '../../lib/glossary';
import { IconInfo } from '../icons';

const TIP_WIDTH = 300;
const EDGE = 12;

/**
 * A glossary term with a plain-language explanation, shown on hover, focus or tap. Keyboard and
 * screen-reader friendly: the explanation is the button's description and Escape closes it.
 */
export function Term({ id: key, label, chip }: { id: string; label?: string; chip?: boolean }) {
  const entry = glossary(key);
  const tipId = useId();
  const [open, setOpen] = useState(false);
  const [place, setPlace] = useState<CSSProperties>({});
  if (!entry) return <>{label ?? key}</>;
  // Place the tip under the term, shifted just enough to stay fully inside the viewport.
  const show = (el: HTMLElement) => {
    const r = el.getBoundingClientRect();
    const width = Math.min(TIP_WIDTH, window.innerWidth - EDGE * 2);
    const left = Math.min(0, window.innerWidth - EDGE - width - r.left);
    setPlace({ left: Math.max(left, EDGE - r.left), width });
    setOpen(true);
  };
  return (
    <span className={`term${chip ? ' term-chip' : ''}${open ? ' open' : ''}`} onMouseEnter={(e) => show(e.currentTarget)} onMouseLeave={() => setOpen(false)}>
      <button
        type="button"
        className="term-btn"
        aria-describedby={tipId}
        aria-expanded={open}
        onClick={(e) => (open ? setOpen(false) : show(e.currentTarget.parentElement ?? e.currentTarget))}
        onFocus={(e) => show(e.currentTarget.parentElement ?? e.currentTarget)}
        onBlur={() => setOpen(false)}
        onKeyDown={(e) => e.key === 'Escape' && setOpen(false)}
      >
        {label ?? entry.term}
        <IconInfo width={12} height={12} aria-hidden="true" />
      </button>
      <span role="tooltip" id={tipId} className="term-tip" style={place}>
        <strong>{entry.term}</strong>
        {entry.definition}
      </span>
    </span>
  );
}
