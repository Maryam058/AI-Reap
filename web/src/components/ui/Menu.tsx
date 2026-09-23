import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { IconMore } from '../icons';

export interface MenuItem {
  key: string;
  label: string;
  icon?: ReactNode;
  to?: string;
  onSelect?: () => void;
  danger?: boolean;
  disabled?: boolean;
  hint?: string;
}

/** Accessible "⋯" action menu. Items are links when `to` is set, buttons otherwise. */
export function Menu({ items, label, trigger }: { items: MenuItem[]; label: string; trigger?: ReactNode }) {
  const [open, setOpen] = useState(false);
  const wrap = useRef<HTMLDivElement>(null);
  const id = useId();

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (!wrap.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setOpen(false);
        wrap.current?.querySelector<HTMLElement>('[aria-haspopup]')?.focus();
      }
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        const els = Array.from(wrap.current?.querySelectorAll<HTMLElement>('[role="menuitem"]:not([aria-disabled="true"])') ?? []);
        if (els.length === 0) return;
        e.preventDefault();
        const i = els.indexOf(document.activeElement as HTMLElement);
        els[(i + (e.key === 'ArrowDown' ? 1 : -1) + els.length) % els.length].focus();
      }
    };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    wrap.current?.querySelector<HTMLElement>('[role="menuitem"]:not([aria-disabled="true"])')?.focus();
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  return (
    <div className="menu" ref={wrap}>
      <button
        type="button"
        className={trigger ? 'btn btn-secondary' : 'icon-btn'}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? id : undefined}
        aria-label={trigger ? undefined : label}
        title={trigger ? undefined : label}
        onClick={(e) => {
          e.preventDefault();
          setOpen((o) => !o);
        }}
      >
        {trigger ?? <IconMore />}
      </button>
      {open && (
        <div className="menu-list" role="menu" id={id} aria-label={label}>
          {items.map((item) => {
            const cls = `menu-item${item.danger ? ' danger' : ''}`;
            const content = (
              <>
                {item.icon}
                <span>{item.label}</span>
              </>
            );
            if (item.to && !item.disabled) {
              return (
                <Link key={item.key} to={item.to} role="menuitem" className={cls} onClick={() => setOpen(false)}>
                  {content}
                </Link>
              );
            }
            return (
              <button
                key={item.key}
                type="button"
                role="menuitem"
                className={cls}
                aria-disabled={item.disabled || undefined}
                title={item.hint}
                onClick={() => {
                  if (item.disabled) return;
                  setOpen(false);
                  item.onSelect?.();
                }}
              >
                {content}
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}
