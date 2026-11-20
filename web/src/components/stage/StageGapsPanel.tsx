import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import type { LifecycleCheck } from '../../api/types';
import { useProject } from '../../context/ProjectContext';
import { stageLabel } from '../../lib/lifecycle';
import { stageByKey, stageChecks, stageStatus, type StageGapItem, type StageKey } from '../../lib/stages';
import { phasePath } from '../../lib/workAreas';
import { CheckList } from '../CheckList';
import { IconAlert, IconArrowRight, IconCheck, IconCheckCircle, IconRefresh, IconSparkles } from '../icons';

const keyOf = (i: StageGapItem<LifecycleCheck>) => `${i.lifecycleStatus}|${i.check.label}`;

/** Failing checks grouped by the lifecycle status they gate, in lifecycle order. */
function byLifecycleStatus(items: StageGapItem<LifecycleCheck>[]): [number, LifecycleCheck[]][] {
  const groups = new Map<number, LifecycleCheck[]>();
  for (const i of items) groups.set(i.lifecycleStatus, [...(groups.get(i.lifecycleStatus) ?? []), i.check]);
  return [...groups.entries()].sort((a, b) => a[0] - b[0]);
}

/**
 * What a work stage still needs, at the top of its page: the backend's exit checks that concern
 * this stage (lib/stages attributes them), each with what blocks it, who can fix it and a link to
 * the place it is fixed (CheckGuidance). Two kinds:
 *   - "Needs attention": checks of a lifecycle status the project already passed that no longer hold;
 *   - "To finish this stage": the current lifecycle status's exit checks, met and not met.
 * The list follows the shared lifecycle state, which is re-read after every artifact change, so a
 * fixed check moves to "Resolved" on its own. Mount it keyed by project and stage, so that list
 * starts afresh on each visit. Nothing here moves the lifecycle: moving on stays on
 * the overview's lifecycle panel, behind the backend's gates.
 */
export function StageGapsPanel({ stageKey }: { stageKey: StageKey }) {
  const { project, lifecycle, lifecycleFailed, reloadLifecycle } = useProject();
  const stage = stageByKey(stageKey)!;
  const items = stageChecks(stage, project.status, lifecycle);
  const gaps = items.filter((i) => i.kind === 'attention' && !i.check.passed);
  const todo = items.filter((i) => i.kind === 'todo');
  const todoOpen = todo.filter((i) => !i.check.passed);
  const failingKeys = [...gaps, ...todoOpen].map(keyOf);
  const failingSig = failingKeys.join('\n');

  // Every check seen failing during this visit, so one that passes afterwards can be shown as resolved.
  const [seen, setSeen] = useState<Map<string, string>>(() => new Map());
  useEffect(() => {
    if (!failingSig) return;
    setSeen((prev) => {
      const missing = [...gaps, ...todoOpen].filter((i) => !prev.has(keyOf(i)));
      if (missing.length === 0) return prev;
      const next = new Map(prev);
      for (const i of missing) next.set(keyOf(i), i.check.label);
      return next;
    });
    // failingSig captures the failing set; the arrays themselves are rebuilt every render.
  }, [failingSig]);
  // "Checking…" lasts until a newer lifecycle state (or a failure) arrives.
  const [checkingFrom, setCheckingFrom] = useState<object | null | undefined>(undefined);
  const checking = checkingFrom !== undefined && checkingFrom === lifecycle && !lifecycleFailed;
  const recheck = () => {
    setCheckingFrom(lifecycle);
    reloadLifecycle();
  };

  if (stage.kind === 'tool') return null;
  if (lifecycleFailed && !lifecycle) {
    return (
      <div className="alert alert-warning stage-gaps-error" role="status">
        <IconAlert width={16} height={16} />
        <span>We couldn’t check what this stage still needs.</span>
        <button type="button" className="btn btn-sm btn-secondary" onClick={recheck}>
          Try again
        </button>
      </div>
    );
  }
  if (!lifecycle) return <div className="stage-gaps-loading skeleton skeleton-line" aria-label="Checking what this stage needs" />;

  const status = stageStatus(stage, project.status, lifecycle);
  const current = new Set(failingKeys);
  const resolved = [...seen.entries()].filter(([k]) => !current.has(k)).map(([, label]) => label);
  const canOverride = lifecycle.actions.canOverride;
  const milestones = stage.serves.map(stageLabel).join(' & ');
  const firstServed = Math.min(...stage.serves);

  if (status === 'not-started') {
    return (
      <p className="stage-gaps-note muted">
        This stage’s exit checks apply once the project reaches <strong>{stageLabel(firstServed)}</strong> (it is in {stageLabel(project.status)} now). You can prepare its work at any time.
      </p>
    );
  }

  const outstanding = gaps.length + todoOpen.length;
  const copilotQ = encodeURIComponent(
    `What is missing for the ${stage.name} stage, and how do I resolve it? Open items: ${[...gaps, ...todoOpen].map((i) => i.check.label).join('; ')}`.slice(0, 480),
  );

  return (
    <section className={`panel stage-gaps${gaps.length > 0 ? ' has-attention' : ''}`} aria-labelledby={`stage-gaps-${stageKey}`} id="stage-gaps">
      <header className="stage-gaps-head">
        <div>
          <h2 id={`stage-gaps-${stageKey}`}>
            {gaps.length > 0 ? (
              <>
                <IconAlert width={17} height={17} aria-hidden="true" /> Needs attention
              </>
            ) : outstanding > 0 ? (
              'What this stage still needs'
            ) : (
              <>
                <IconCheckCircle width={17} height={17} aria-hidden="true" /> Nothing outstanding
              </>
            )}
          </h2>
          <p className="muted">
            {gaps.length > 0
              ? `The project already passed ${milestones}, but ${gaps.length === 1 ? 'this check no longer holds' : `these ${gaps.length} checks no longer hold`}. Each one says what is wrong, who can fix it and where.`
              : outstanding > 0
                ? `${outstanding} of this stage’s exit checks ${outstanding === 1 ? 'is' : 'are'} not met yet. Each one says what is needed, who can do it and where.`
                : status === 'completed'
                  ? `Every exit check of ${milestones} that this stage covers still holds.`
                  : 'Every exit check this stage covers is met.'}
          </p>
        </div>
        <div className="stage-gaps-tools">
          {outstanding > 0 && (
            <span className={`stage-gaps-count${gaps.length > 0 ? ' warn' : ''}`} aria-label={`${outstanding} outstanding`}>
              {outstanding}
              <small>open</small>
            </span>
          )}
          <button type="button" className="btn btn-sm btn-ghost" onClick={recheck} disabled={checking} aria-busy={checking}>
            <IconRefresh width={14} height={14} /> {checking ? 'Checking…' : 'Re-check'}
          </button>
          {outstanding > 0 && (
            <Link to={`${phasePath(project.id, 'copilot')}?phase=${stageKey}&q=${copilotQ}`} className="btn btn-sm btn-ghost">
              <IconSparkles width={14} height={14} /> Ask Copilot
            </Link>
          )}
        </div>
      </header>

      {byLifecycleStatus(gaps).map(([ls, checks]) => (
        <div key={`gap-${ls}`} className="stage-gaps-group">
          <h3>
            {stageLabel(ls)} exit checks · <span className="text-warning">{checks.length} no longer met</span>
          </h3>
          <CheckList checks={checks} projectId={project.id} canOverride={canOverride} />
        </div>
      ))}

      {todo.length > 0 && (
        <div className="stage-gaps-group">
          <h3>
            To finish this stage · {todo.length - todoOpen.length} of {todo.length} met
          </h3>
          <CheckList checks={todo.map((i) => i.check)} projectId={project.id} canOverride={canOverride} expandFirst={gaps.length === 0} />
        </div>
      )}

      {resolved.length > 0 && (
        <div className="stage-gaps-resolved" role="status">
          <h3>
            <IconCheck width={13} height={13} strokeWidth={3} aria-hidden="true" /> Resolved during this visit
          </h3>
          <ul>
            {resolved.map((label) => (
              <li key={label}>{label}</li>
            ))}
          </ul>
        </div>
      )}

      {outstanding === 0 && resolved.length > 0 && (
        <div className="alert alert-success stage-gaps-success" role="status">
          <IconCheckCircle width={16} height={16} />
          <span>
            <strong>All gaps in {stage.name} are resolved.</strong>{' '}
            {status === 'completed'
              ? 'The stage is completed again and every progress indicator has been updated.'
              : 'Every exit check this stage covers is met.'}
          </span>
        </div>
      )}

      {todo.length > 0 && todoOpen.length === 0 && gaps.length === 0 && (
        <p className="stage-gaps-next">
          The project can now leave {stageLabel(project.status)} once every other stage serving it is ready.{' '}
          <Link to={`${phasePath(project.id)}#lifecycle`}>
            Go to lifecycle status &amp; approvals <IconArrowRight width={12} height={12} />
          </Link>
        </p>
      )}
    </section>
  );
}
