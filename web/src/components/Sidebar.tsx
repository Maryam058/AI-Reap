import { useEffect, useState, type CSSProperties, type FocusEvent, type MouseEvent } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { useSidebar } from '../context/SidebarContext';
import { readLastProject, useProjectContext } from '../context/ProjectContext';
import { NAV_GROUPS, SEGMENT_DEFAULT_TAB, type NavGroup, type NavModule } from '../navigation/navConfig';
import { phasePath } from '../lib/workAreas';
import { LAST_STAGE, lifecycleProgress, LIFECYCLE_STAGES, stageLabel, stageState, type StageState } from '../lib/lifecycle';
import { useProjectLifecycle } from '../hooks/useLifecycle';
import { initials, statusLabel, statusVariant } from '../lib/format';
import { IconCheck, IconChevronDown, IconFolder, IconPanelLeft, IconSelector, IconSparkles, IconX } from './icons';

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
    // Modules sharing one route are told apart by tab. The agent pipeline owns its own tab;
    // every other assistant tab (documents, audit) belongs to the assistant.
    const fallback = SEGMENT_DEFAULT_TAB[mod.segment];
    if (!fallback) return true;
    if (mod.segment === 'copilot') return (tab === 'pipeline') === (mod.tab === 'pipeline');
    return (tab ?? fallback) === (mod.tab ?? fallback);
  };

  /** Where the project's lifecycle stage is relative to the stages a module serves; only once a project is open. */
  const moduleState = (mod: NavModule): StageState | undefined => {
    if (!project || !mod.stages) return undefined;
    const states = mod.stages.map((s) => stageState(s, project.status));
    if (states.every((s) => s === 'completed')) return 'completed';
    return states.includes('current') ? 'current' : 'upcoming';
  };

  const showTip = (label: string) => (e: MouseEvent<HTMLElement> | FocusEvent<HTMLElement>) => {
    if (!rail) return;
    const r = e.currentTarget.getBoundingClientRect();
    setTip({ label, top: r.top + r.height / 2, left: r.right + 10 });
  };
  const hideTip = () => setTip(null);

  /** `pipeline`: the group is drawn as a lifecycle stepper, the stage marker standing in for the icon. */
  const renderModule = (mod: NavModule, pipeline: boolean) => {
    const Icon = mod.icon;
    const active = isActive(mod);
    const suffix = mod.tab && mod.tab !== SEGMENT_DEFAULT_TAB[mod.segment ?? ''] ? `?tab=${mod.tab}` : '';
    const to = mod.to ?? (mod.status !== 'planned' && targetProjectId && mod.segment ? `${phasePath(targetProjectId, mod.segment)}${suffix}` : null);
    const state = moduleState(mod);
    const planned = mod.status === 'planned';
    const needsProject = !to && !planned;
    const tipLabel = planned ? `${mod.label} (planned)` : mod.label;
    const step = pipeline && state;
    // Hover text names the lifecycle stage(s) the module's work serves, so nav names map onto stages.
    const hint = mod.stages ? `${mod.description} Lifecycle stage: ${mod.stages.map(stageLabel).join(' & ')}.` : mod.description;

    const content = (
      <>
        {step ? (
          <span
            className={`nav-step ${state}`}
            {...(state === 'upcoming' ? { 'aria-hidden': true } : { role: 'img', 'aria-label': state === 'completed' ? 'Lifecycle stage passed' : 'Current lifecycle stage' })}
          >
            {state === 'completed' && <IconCheck width={11} height={11} strokeWidth={3.25} />}
          </span>
        ) : (
          <Icon />
        )}
        {!rail && (
          <>
            <span className="nav-label">{mod.label}</span>
            {planned && <span className="nav-soon">Soon</span>}
            {step && state === 'current' && <span className="nav-step-tag">In progress</span>}
            {!step && state === 'completed' && (
              <span className="nav-state completed" role="img" aria-label="Lifecycle stage passed">
                <IconCheck width={10} height={10} strokeWidth={3.5} />
              </span>
            )}
            {!step && state === 'current' && <span className="nav-state current" role="img" aria-label="Current lifecycle stage" />}
          </>
        )}
        {rail && !step && state === 'current' && <span className="nav-rail-dot" aria-hidden="true" />}
      </>
    );

    const cls = `nav-link${active ? ' active' : ''}${mod.ai ? ' ai' : ''}${to ? '' : ' disabled'}`;
    return (
      <li key={mod.key} className={`nav-item${step ? ` step-${state}` : ''}`} onMouseEnter={showTip(tipLabel)} onMouseLeave={hideTip} onFocus={showTip(tipLabel)} onBlur={hideTip}>
        {to ? (
          <Link to={to} className={cls} aria-label={rail ? mod.label : undefined} aria-current={active ? 'page' : undefined} title={rail ? undefined : hint}>
            {content}
          </Link>
        ) : (
          <span className={cls} aria-disabled="true" title={rail ? undefined : needsProject ? 'Open a project to use this step' : hint}>
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
    const meta = group.key === 'sdlc' && progress ? `${progress.percent}%` : null;
    // A group whose every step maps to lifecycle stages reads as a pipeline once a project is open.
    const pipeline = !!project && modules.every((m) => m.stages);
    return (
      <section className={`nav-group${pipeline ? ' pipeline' : ''}`} key={group.key} aria-label={group.label}>
        {rail ? (
          <div className="nav-rail-sep" />
        ) : group.collapsible ? (
          <button type="button" className="nav-group-toggle" aria-expanded={open} onClick={() => toggleSection(group.key)}>
            <span className="nav-group-text">{group.label}</span>
            <IconChevronDown className={`nav-group-chevron${open ? '' : ' closed'}`} width={12} height={12} />
            {group.key === 'ai' && (
              <span className="nav-ai-chip" aria-hidden="true">
                <IconSparkles width={9} height={9} /> AI
              </span>
            )}
            {meta && <span className="nav-group-meta">{meta}</span>}
          </button>
        ) : (
          <div className="nav-group-label">{group.label}</div>
        )}
        <div className={`nav-collapse${open ? ' open' : ''}`}>
          <div className="nav-collapse-inner" {...(open ? {} : { inert: true })}>
            <ul className="nav-list">{modules.map((m) => renderModule(m, pipeline))}</ul>
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

/** The open project's lifecycle position: stage, 8-segment track, and any pending Reviewer request. */
function SidebarLifecycle({ projectId, status, stamp }: { projectId: string; status: number; stamp: string }) {
  const { state } = useProjectLifecycle(projectId, `${status}|${stamp}`);
  const progress = lifecycleProgress(status);
  const pending = !!state?.pendingTransition;
  return (
    <>
      <div className="nav-project-meta">
        <span>
          Stage {progress.stageNumber} of {LIFECYCLE_STAGES.length}
        </span>
        <span className={`status-pill tone-${statusVariant(status)}`}>{statusLabel(status)}</span>
      </div>
      <ol className={`phase-track${progress.finished ? ' finished' : ''}`} aria-label="Project lifecycle">
        {LIFECYCLE_STAGES.map((s) => {
          // As in LifecycleStepper: the final stage, once reached, is finished rather than current.
          const reached = stageState(s.status, status);
          const st = reached === 'current' && s.status === LAST_STAGE ? 'completed' : reached;
          return <li key={s.status} className={`phase-seg ${st}${pending && st === 'current' ? ' awaiting' : ''}`} title={`${s.status + 1}. ${s.label}`} />;
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
            <span className="nav-state current" aria-hidden="true" /> Now: <strong>{stageLabel(status)}</strong>
          </>
        )}
      </div>
    </>
  );
}
