import { useEffect, useRef } from 'react';
import { Link, useLocation } from 'react-router-dom';
import type { Project } from '../../api/types';
import { activeStages, STAGE_COUNT, STAGE_STATUS_LABEL, STAGES, stageGaps, stageStatus, type StageSignals, type StageStatus } from '../../lib/stages';
import { phasePath } from '../../lib/workAreas';
import { IconAlert, IconCheck, IconGrid } from '../icons';
import { StageIcon } from './StageIcon';

/** Shorter status text where the navigator is narrow; the full label stays in the accessible name. */
const SHORT: Record<StageStatus, string> = {
  'not-started': 'Not started',
  'in-progress': 'In progress',
  awaiting: 'Awaiting approval',
  attention: 'Needs attention',
  completed: 'Completed',
  available: 'Anytime',
  ready: 'Ready',
};

/**
 * Persistent nine-stage navigation for a project: every stage is always reachable (the app never
 * locked stages), the page being viewed is highlighted, and the stage the project's current work
 * happens in is marked "Now". Horizontal on desktop; a scrollable strip on small screens.
 */
export function StageNavigator({ project, signals }: { project: Project; signals: StageSignals | null }) {
  const { pathname } = useLocation();
  const list = useRef<HTMLOListElement>(null);
  const segment = pathname.split('/')[3] ?? '';
  const now = new Set(activeStages(project.status).map((s) => s.key));
  const overview = phasePath(project.id);

  // Keep the viewed stage visible in the scrollable strip, without scrolling the page itself.
  useEffect(() => {
    const el = list.current;
    const active = el?.querySelector<HTMLElement>('[aria-current="page"]');
    if (!el || !active || el.scrollWidth <= el.clientWidth) return;
    el.scrollLeft = active.offsetLeft - el.clientWidth / 2 + active.clientWidth / 2;
  }, [segment]);

  return (
    <nav className="stage-nav" aria-label="Project stages">
      <Link to={overview} className={`stage-nav-home${segment === '' ? ' active' : ''}`} aria-current={segment === '' ? 'page' : undefined}>
        <IconGrid width={16} height={16} aria-hidden="true" />
        <span>Overview</span>
      </Link>
      <ol className="stage-nav-list" ref={list}>
        {STAGES.map((s) => {
          const status = stageStatus(s, project.status, signals);
          const gaps = status === 'attention' ? stageGaps(s, project.status, signals).length : 0;
          const active = segment === s.segment;
          const isNow = now.has(s.key);
          return (
            <li key={s.key} className={`stage-nav-item st-${status}${active ? ' active' : ''}${isNow ? ' now' : ''}${s.kind === 'tool' ? ' tool' : ''}`}>
              <Link
                to={phasePath(project.id, s.segment)}
                aria-current={active ? 'page' : undefined}
                aria-label={`Stage ${s.number} of ${STAGE_COUNT}: ${s.name}, ${STAGE_STATUS_LABEL[status]}${gaps > 0 ? `, ${gaps} ${gaps === 1 ? 'gap' : 'gaps'} to resolve` : ''}${isNow ? ', current work' : ''}`}
                title={gaps > 0 ? `${s.name}: ${gaps} ${gaps === 1 ? 'gap' : 'gaps'} to resolve — open to see and fix ${gaps === 1 ? 'it' : 'them'}` : s.summary}
              >
                <span className="stage-nav-node" aria-hidden="true">
                  <StageIcon stage={s.key} width={15} height={15} />
                  {status === 'completed' && (
                    <span className="stage-nav-badge done">
                      <IconCheck width={8} height={8} strokeWidth={4} />
                    </span>
                  )}
                  {status === 'attention' && (
                    <span className="stage-nav-badge warn">{gaps > 0 ? <span className="stage-nav-badge-num">{gaps}</span> : <IconAlert width={8} height={8} strokeWidth={3} />}</span>
                  )}
                </span>
                <span className="stage-nav-text" aria-hidden="true">
                  <span className="stage-nav-name">
                    <span className="stage-nav-num">{s.number}</span> {s.name}
                  </span>
                  <span className="stage-nav-status">{isNow && status !== 'ready' ? `Now · ${SHORT[status]}` : SHORT[status]}</span>
                </span>
              </Link>
            </li>
          );
        })}
      </ol>
    </nav>
  );
}

/** Nine-segment strip for cards and table rows, from the same derived stage statuses. */
export function StageDots({ status, signals }: { status: number; signals?: StageSignals | null }) {
  return (
    <ul className="stage-dots" aria-hidden="true">
      {STAGES.map((s) => {
        const st = stageStatus(s, status, signals);
        return <li key={s.key} className={`st-${st}`} title={`${s.number}. ${s.name}: ${STAGE_STATUS_LABEL[st]}`} />;
      })}
    </ul>
  );
}
