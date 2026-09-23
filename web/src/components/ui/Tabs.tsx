import type { KeyboardEvent, ReactNode } from 'react';

export interface TabItem {
  key: string;
  label: string;
  icon?: ReactNode;
  count?: number;
}

/** Controlled, keyboard-navigable tab list. Callers render the matching panel with role="tabpanel". */
export function Tabs({ items, active, onChange, label }: { items: TabItem[]; active: string; onChange: (key: string) => void; label: string }) {
  const onKey = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
    const i = items.findIndex((t) => t.key === active);
    const next = items[(i + (e.key === 'ArrowRight' ? 1 : -1) + items.length) % items.length];
    onChange(next.key);
    requestAnimationFrame(() => document.getElementById(`tab-${next.key}`)?.focus());
  };

  return (
    <div className="tabs" role="tablist" aria-label={label} onKeyDown={onKey}>
      {items.map((t) => (
        <button
          key={t.key}
          id={`tab-${t.key}`}
          type="button"
          role="tab"
          aria-selected={t.key === active}
          aria-controls={`panel-${t.key}`}
          tabIndex={t.key === active ? 0 : -1}
          className={`tab${t.key === active ? ' active' : ''}`}
          onClick={() => onChange(t.key)}
        >
          {t.icon}
          {t.label}
          {t.count !== undefined && <span className="tab-count">{t.count}</span>}
        </button>
      ))}
    </div>
  );
}
