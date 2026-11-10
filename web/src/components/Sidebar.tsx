import { useEffect, useState, type CSSProperties, type FocusEvent, type MouseEvent } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { useSidebar } from '../context/SidebarContext';
import { readLastProject, useProjectContext } from '../context/ProjectContext';
import { ALL_MODULES, NAV_GROUPS, SEGMENT_DEFAULT_TAB, type NavGroup, type NavModule } from '../navigation/navConfig';
import { phasePath } from '../lib/workAreas';
import { lifecycleProgress, stageLabel } from '../lib/lifecycle';
import { activeStages, activeStagesLabel, STAGE_COUNT, STAGE_STATUS_LABEL, stageByKey, STAGES, stageStatus, type StageStatus } from '../lib/stages';
import { useProjectLifecycle } from '../hooks/useLifecycle';
import { initials, statusLabel, statusVariant } from '../lib/format';
import { IconAlert, IconCheck, IconChevronDown, IconFolder, IconPanelLeft, IconSelector, IconX } from './icons';

const SECTIONS_KEY = 'aireap.nav-sections-closed';

function useIsMobile(): boolean {
  const query = '(max-width: 900px)';
  const [mobile, setMobile] = useState(() => window.matchMedia(query).matches);
  useEffect(() => {
    const mq = window.matchMedia(query);
    const onChange = () => setMobile(mq.matches);
    mq.addEventListener('change', onChange);
    return () => mq.removeEventListener('change', onChange);
  }, []);
  return mobile;
}

function loadClosed(): Record<string, boolean> {
  try {
    return JSON.parse(localStorage.getItem(SECTIONS_KEY) ?? '{}') as Record<string, boolean>;
  } catch {
    return {};
  }
}

interface Tip {
  label: string;
  top: number;
  left: number;
}

export function Sidebar() {
  const { pathname, search } = useLocation();
  const { collapsed, toggleCollapsed, mobileOpen, closeMobile } = useSidebar();
  const { hasRole } = useAuth();
  const { projectId, project } = useProjectContext();
  const isMobile = useIsMobile();
  const rail = collapsed && !isMobile;
  const [closed, setClosed] = useState<Record<string, boolean>>(loadClosed);
  const [tip, setTip] = useState<Tip | null>(null);

  useEffect(() => {
    closeMobile();
    setTip(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pathname]);

  const toggleSection = (key: string) =>
    setClosed((prev) => {
      const next = { ...prev, [key]: !prev[key] };
      try {
        localStorage.setItem(SECTIONS_KEY, JSON.stringify(next));
      } catch {
        // best effort
      }
      return next;
    });

  // Outside a project the workflow links point at the last project the user opened, if any.
  const last = projectId ? null : readLastProject();
  const targetProjectId = projectId ?? last?.id ?? null;
  const progress = project ? lifecycleProgress(project.status) : null;
  const tab = new URLSearchParams(search).get('tab');

  const isActive = (mod: NavModule): boolean => {
    // The project list, the create form, and a project's overview/edit pages all belong to Projects.
    if (mod.key === 'projects') return pathname === '/projects' || /^\/projects\/[^/]+(\/edit)?\/?$/.test(pathname);
    if (mod.to) return pathname === mod.to || pathname.startsWith(`${mod.to}/`);
    if (!projectId || !mod.segment || !pathname.startsWith(phasePath(projectId, mod.segment))) return false;
    // Modules sharing one route are told apart by tab: a tool owns its tab (stakeholders, agent
    // pipeline); the stage owns every other tab of its route.
    if (mod.tab) return tab === mod.tab;
    return !ALL_MODULES.some((m) => m.tab && m.segment === mod.segment && m.tab === tab);
  };

  /** The derived status of a stage entry; only once a project is open. */
  const moduleStatus = (mod: NavModule): StageStatus | undefined => (project && mod.stageKey ? stageStatus(stageByKey(mod.stageKey)!, project.status) : undefined);
  const nowKeys = new Set(project ? activeStages(project.status).map((s) => s.key) : []);

  const showTip = (label: string) => (e: MouseEvent<HTMLElement> | FocusEvent<HTMLElement>) => {
    if (!rail) return;
    const r = e.currentTarget.getBoundingClientRect();
    setTip({ label, top: r.top + r.height / 2, left: r.right + 10 });
  };
  const hideTip = () => setTip(null);

  const renderModule = (mod: NavModule) => {
    const Icon = mod.icon;
    const active = isActive(mod);
    const suffix = mod.tab && mod.tab !== SEGMENT_DEFAULT_TAB[mod.segment ?? ''] ? `?tab=${mod.tab}` : '';
    const to = mod.to ?? (mod.status !== 'planned' && targetProjectId && mod.segment ? `${phasePath(targetProjectId, mod.segment)}${suffix}` : null);
    const status = moduleStatus(mod);
    const stage = mod.stageKey ? stageByKey(mod.stageKey) : undefined;
    const isNow = !!mod.stageKey && nowKeys.has(mod.stageKey);
    const planned = mod.status === 'planned';
    const needsProject = !to && !planned;
    const tipLabel = stage ? `${stage.number}. ${mod.label}${status ? ` — ${STAGE_STATUS_LABEL[status]}` : ''}` : planned ? `${mod.label} (planned)` : mod.label;

    const content = (
      <>
        {stage ? (
          <span className={`nav-stage-node${status ? ` st-${status}` : ''}${isNow ? ' now' : ''}`} aria-hidden="true">
            {status === 'completed' ? <IconCheck width={11} height={11} strokeWidth={3.25} /> : status === 'attention' ? <IconAlert width={11} height={11} strokeWidth={2.5} /> : stage.number}
          </span>
        ) : (
          <Icon />
        )}
        {!rail && (
          <>
            <span className="nav-label">{mod.label}</span>
            {status && <span className="sr-only">, {STAGE_STATUS_LABEL[status]}</span>}
            {planned && <span className="nav-soon">Soon</span>}
            {isNow && status !== 'ready' && <span className="nav-step-tag">Now</span>}
          </>
        )}
        {rail && isNow && <span className="nav-rail-dot" aria-hidden="true" />}
      </>
    );

    const cls = `nav-link${active ? ' active' : ''}${mod.ai ? ' ai' : ''}${to ? '' : ' disabled'}`;
    return (
      <li key={mod.key} className={`nav-item${stage ? ' nav-stage' : ''}`} onMouseEnter={showTip(tipLabel)} onMouseLeave={hideTip} onFocus={showTip(tipLabel)} onBlur={hideTip}>
        {to ? (
          <Link to={to} className={cls} aria-label={rail ? mod.label : undefined} aria-current={active ? 'page' : undefined} title={rail ? undefined : mod.description}>
            {content}
          </Link>
        ) : (
          <span className={cls} aria-disabled="true" title={rail ? undefined : needsProject ? 'Open a project to use this' : mod.description}>
            {content}
          </span>
        )}
      </li>
    );
  };

  const renderGroup = (group: NavGroup) => {
    const modules = group.modules.filter((m) => !m.role || hasRole(m.role));
    if (modules.length === 0) return null;
    const open = rail || !group.collapsible || !closed[group.key];
    const meta = group.key === 'lifecycle' && progress ? `${progress.percent}%` : null;
    return (
      <section className={`nav-group nav-group-${group.key}`} key={group.key} aria-label={group.label}>
        {rail ? (
          <div className="nav-rail-sep" />
        ) : group.collapsible ? (
          <button type="button" className="nav-group-toggle" aria-expanded={open} onClick={() => toggleSection(group.key)}>
            <span className="nav-group-text">{group.label}</span>
            <IconChevronDown className={`nav-group-chevron${open ? '' : ' closed'}`} width={12} height={12} />
            {meta && (
              <span className="nav-group-meta" title="Overall lifecycle progress">
                {meta}
              </span>
            )}
          </button>
        ) : (
          <div className="nav-group-label">{group.label}</div>
        )}
        <div className={`nav-collapse${open ? ' open' : ''}`}>
          <div className="nav-collapse-inner" {...(open ? {} : { inert: true })}>
            {!rail && group.hint && <p className="nav-group-hint">{group.hint}</p>}
            <ul className="nav-list">{modules.map((m) => renderModule(m))}</ul>
          </div>
        </div>
      </section>
    );
  };

  // Only shown while a project is open; outside a project the sidebar has no project card.
  // In the rail it shrinks to the project's initials inside a lifecycle-progress ring.
  const overviewPath = projectId ? `/projects/${projectId}` : null;
  const projectCard =
    overviewPath &&
    (rail ? (
      <div className="nav-rail-project">
        <Link
          to={overviewPath}
          className={`nav-project-ring${pathname === overviewPath ? ' current' : ''}`}
          style={{ '--p': `${progress?.percent ?? 0}%` } as CSSProperties}
          aria-label={`Current project: ${project?.name ?? 'loading'}`}
          onMouseEnter={showTip(project?.name ?? 'Current project')}
          onMouseLeave={hideTip}
          onFocus={showTip(project?.name ?? 'Current project')}
          onBlur={hideTip}
        >
          <span className="nav-project-avatar">{project ? initials(project.name) : ''}</span>
        </Link>
      </div>
    ) : (
      <div className="nav-project">
        <div className="nav-project-head">
          <Link to={overviewPath} className={`nav-project-id${pathname === overviewPath ? ' current' : ''}`} title={project?.name}>
            <span className="nav-project-avatar" aria-hidden="true">
              {project ? initials(project.name) : <IconFolder width={14} height={14} />}
            </span>
            <span className="nav-project-text">
              {project ? <span className="nav-project-name">{project.name}</span> : <span className="skeleton skeleton-line" />}
              <span className="nav-project-label">Current project</span>
            </span>
          </Link>
          <Link to="/projects" className="nav-project-switch" aria-label="Switch project" title="Switch project">
            <IconSelector width={15} height={15} />
          </Link>
        </div>
        {project && progress && <SidebarLifecycle projectId={project.id} status={project.status} stamp={project.updatedAt} />}
      </div>
    ));

  return (
    <>
      {mobileOpen && <div className="sidebar-scrim" onClick={closeMobile} />}
      <aside className={`sidebar${collapsed ? ' collapsed' : ''}${rail ? ' rail' : ''}${mobileOpen ? ' mobile-open' : ''}`} aria-label="Primary navigation">
        <div className="sidebar-brand-row">
          <Link to="/dashboard" className="sidebar-brand" aria-label="AI-REAP dashboard" onMouseEnter={showTip('AI-REAP')} onMouseLeave={hideTip} onFocus={showTip('AI-REAP')} onBlur={hideTip}>
            <span className="sidebar-logo">AI</span>
            {!rail && (
              <span className="sidebar-brand-text">
                <span className="sidebar-brand-name">AI-REAP</span>
                <span className="sidebar-brand-tag">AI Requirements Engineering &amp; SDLC Automation</span>
              </span>
            )}
          </Link>
          {!rail && (
            <button type="button" className="sidebar-collapse-btn" onClick={toggleCollapsed} aria-label="Collapse navigation" aria-expanded={true} title="Collapse navigation">
              <IconPanelLeft width={17} height={17} />
            </button>
          )}
          <button className="sidebar-mobile-close" onClick={closeMobile} aria-label="Close navigation">
            <IconX />
          </button>
        </div>

        <nav className="sidebar-scroll" aria-label="Main" onScroll={hideTip}>
          {projectCard}
          {/* Outside a project the work-area links open the last project; say which one. */}
          {!rail && last && (
            <p className="nav-last-project">
              Project links open your last project:{' '}
              <Link to={`/projects/${last.id}`} title={last.name}>
                {last.name}
              </Link>
            </p>
          )}
          {NAV_GROUPS.map(renderGroup)}
        </nav>

        {rail && (
          <div className="sidebar-footer">
            <button
              type="button"
              className="sidebar-collapse-btn"
              onClick={toggleCollapsed}
              aria-label="Expand navigation"
              aria-expanded={false}
              onMouseEnter={showTip('Expand navigation')}
              onMouseLeave={hideTip}
              onFocus={showTip('Expand navigation')}
              onBlur={hideTip}
            >
              <IconPanelLeft width={17} height={17} />
            </button>
          </div>
        )}
      </aside>
      {tip && rail && (
        <div className="nav-tooltip" role="tooltip" style={{ top: tip.top, left: tip.left }}>
          {tip.label}
        </div>
      )}
    </>
  );
}

/** The open project's position: overall progress, the nine stages as a track, and any pending Reviewer request. */
function SidebarLifecycle({ projectId, status, stamp }: { projectId: string; status: number; stamp: string }) {
  const { state } = useProjectLifecycle(projectId, `${status}|${stamp}`);
  const progress = lifecycleProgress(status);
  const pending = !!state?.pendingTransition;
  return (
    <>
      <div className="nav-project-meta">
        <span title="Overall progress through the lifecycle">Overall progress {progress.percent}%</span>
        <span className={`status-pill tone-${statusVariant(status)}`} title="Lifecycle status">
          {statusLabel(status)}
        </span>
      </div>
      <ol className={`phase-track stages${progress.finished ? ' finished' : ''}`} aria-label={`Project stages, ${STAGE_COUNT} in total`}>
        {STAGES.map((s) => {
          const st = stageStatus(s, status, state);
          return <li key={s.key} className={`phase-seg st-${st}`} title={`${s.number}. ${s.name}: ${STAGE_STATUS_LABEL[st]}`} />;
        })}
      </ol>
      <div className="nav-project-phase">
        {progress.finished ? (
          <span className="nav-project-done">
            <IconCheck width={12} height={12} strokeWidth={3} /> Lifecycle completed · 100%
          </span>
        ) : pending ? (
          <>
            <span className="nav-state current" aria-hidden="true" /> <strong>Awaiting review</strong> to {stageLabel(state!.pendingTransition!.toStatus)}
          </>
        ) : (
          <>
            <span className="nav-state current" aria-hidden="true" /> Now: <strong>{activeStagesLabel(status)}</strong>
          </>
        )}
      </div>
    </>
  );
}
