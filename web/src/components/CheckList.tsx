import { useState } from 'react';
import type { LifecycleCheck } from '../api/types';
import { CheckGuidance } from './CheckGuidance';
import { IconChevronDown } from './icons';

/**
 * A list of lifecycle exit checks, failing first, each failing one expandable into "how to meet
 * this" (ExitCheckGuidanceService). Shared by the lifecycle panel and the stage gap panels, so a
 * check reads the same wherever it is shown.
 */
export function CheckList({
  checks,
  projectId,
  canOverride = false,
  expandFirst = true,
}: {
  checks: LifecycleCheck[];
  projectId: string;
  canOverride?: boolean;
  /** Open the first failing check's guidance by default. */
  expandFirst?: boolean;
}) {
  if (checks.length === 0) return <p className="muted">This stage has no exit checks.</p>;
  // Failing checks first: they are what the owner has to act on.
  const ordered = [...checks].sort((a, b) => Number(a.passed) - Number(b.passed));
  const firstWithGuidance = expandFirst ? ordered.find((c) => !c.passed && c.guidance)?.label : undefined;
  // A check this one follows from is named only while it is failing too (then it carries its key).
  const labelFor = (key: string) => checks.find((c) => c.guidance?.key === key)?.label;
  return (
    <ul className="health-list lc-checks">
      {ordered.map((c) => (
        <CheckRow key={c.label} check={c} projectId={projectId} canOverride={canOverride} defaultOpen={c.label === firstWithGuidance} labelFor={labelFor} />
      ))}
    </ul>
  );
}

// One check. A failing check with guidance expands into "how to meet this"; anything else - a
// passed check, or a check the backend sends without guidance (e.g. a key this client does not
// know) - shows its label and detail exactly as before.
function CheckRow({
  check,
  projectId,
  canOverride,
  defaultOpen,
  labelFor,
}: {
  check: LifecycleCheck;
  projectId: string;
  canOverride: boolean;
  defaultOpen: boolean;
  labelFor: (key: string) => string | undefined;
}) {
  const [open, setOpen] = useState(defaultOpen);
  const guidance = !check.passed && check.guidance && Array.isArray(check.guidance.blockers) ? check.guidance : null;
  return (
    <li className={guidance ? 'lc-check has-guidance' : 'lc-check'}>
      <span className={`health-dot ${check.passed ? 'good' : 'bad'}`} aria-hidden="true" />
      <span className="health-label">
        <strong>{check.label}</strong>
        <small>{check.detail}</small>
        {guidance && (
          <button type="button" className="lc-howto-toggle" aria-expanded={open} onClick={() => setOpen((v) => !v)}>
            {open ? 'Hide how to meet this' : 'How to meet this'}
            <IconChevronDown width={12} height={12} className={open ? 'is-open' : undefined} />
          </button>
        )}
        {guidance && open && <CheckGuidance projectId={projectId} guidance={guidance} canOverride={canOverride} labelFor={labelFor} />}
      </span>
      <span className="sr-only">{check.passed ? 'Met' : 'Not met'}</span>
    </li>
  );
}
