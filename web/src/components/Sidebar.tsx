import { useEffect, useState, type FocusEvent, type MouseEvent } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { useSidebar } from '../context/SidebarContext';
import { readLastProject, useProjectContext } from '../context/ProjectContext';
import { NAV_GROUPS, SEGMENT_DEFAULT_TAB, type NavGroup, type NavModule } from '../navigation/navConfig';
import { deriveSdlc, phasePath, type PhaseState } from '../lib/sdlc';
import { statusLabel, statusVariant } from '../lib/format';
import { IconCheck, IconChevronDown, IconChevronLeft, IconFolder, IconSparkles, IconX } from './icons';

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
  const sdlc = project ? deriveSdlc(project.status) : null;
  const stateByKey: Record<string, PhaseState> = Object.fromEntries((sdlc?.phases ?? []).map((p) => [p.key, p.state]));
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

  /** Combined state across the SDLC phases a module covers; only shown once a project is open. */
  const moduleState = (mod: NavModule): PhaseState | undefined => {
    if (!projectId || !mod.phases) return undefined;
    const states = mod.phases.map((k) => stateByKey[k]).filter(Boolean);
    if (states.length === 0) return undefined;
    if (states.every((s) => s === 'completed')) return 'completed';
    return states.includes('current') ? 'current' : 'upcoming';
  };

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
    const state = moduleState(mod);
    const planned = mod.status === 'planned';
    const needsProject = !to && !planned;
    const tipLabel = planned ? `${mod.label} (planned)` : mod.label;

    const content = (
      <>
        <Icon />
        {!rail && (
          <>
            <span className="nav-label">{mod.label}</span>
            {planned && <span className="nav-soon">Soon</span>}
            {state === 'completed' && (
              <span className="nav-state completed" role="img" aria-label="Phase completed">
                <IconCheck width={10} height={10} strokeWidth={3.5} />
              </span>
            )}
            {state === 'current' && <span className="nav-state current" role="img" aria-label="Current phase" />}
          </>
        )}
        {rail && state === 'current' && <span className="nav-rail-dot" aria-hidden="true" />}
      </>
    );

    const cls = `nav-link${active ? ' active' : ''}${mod.ai ? ' ai' : ''}${to ? '' : ' disabled'}`;
    return (
      <li key={mod.key} className="nav-item" onMouseEnter={showTip(tipLabel)} onMouseLeave={hideTip} onFocus={showTip(tipLabel)} onBlur={hideTip}>
        {to ? (
          <Link to={to} className={cls} aria-label={rail ? mod.label : undefined} aria-current={active ? 'page' : undefined}>
            {content}
          </Link>
        ) : (
          <span className={cls} aria-disabled="true" title={rail ? undefined : needsProject ? 'Open a project to use this step' : mod.description}>
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
    const meta = group.key === 'sdlc' && sdlc ? `${sdlc.percent}%` : null;
    return (
      <section className="nav-group" key={group.key} aria-label={group.label}>
        {rail ? (
          <div className="nav-rail-sep" />
        ) : group.collapsible ? (
          <button type="button" className="nav-group-toggle" aria-expanded={open} onClick={() => toggleSection(group.key)}>
            <IconChevronDown className={`nav-group-chevron${open ? '' : ' closed'}`} width={12} height={12} />
            <span className="nav-group-text">{group.label}</span>
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
            <ul className="nav-list">{modules.map(renderModule)}</ul>
          </div>
        </div>
      </section>
    );
  };

  // Only shown while a project is open; outside a project the sidebar has no project card.
  const projectCard = !rail && projectId && (
    <div className="nav-project active">
      <div className="nav-project-head">
        <IconFolder width={14} height={14} />
        <span className="nav-project-label">Current project</span>
        <Link to="/projects" className="nav-project-switch">
          Switch
        </Link>
      </div>
      {projectId ? (
        project && sdlc ? (
          <>
            <Link to={`/projects/${project.id}`} className={`nav-project-name${pathname === `/projects/${project.id}` ? ' current' : ''}`} title={project.name}>
              {project.name}
            </Link>
            <div className="nav-project-meta">
              <span className={`status-pill tone-${statusVariant(project.status)}`}>{statusLabel(project.status)}</span>
              <span>{sdlc.percent}% SDLC</span>
            </div>
            <ol className="phase-track" aria-label="SDLC phases">
              {sdlc.phases.map((p) => (
                <li key={p.key} className={`phase-seg ${p.state}`} title={`${p.step}. ${p.navLabel} — ${p.state === 'current' ? 'in progress' : p.state}`} />
              ))}
            </ol>
            <div className="nav-project-phase">
              {sdlc.current ? (
                <>
                  <span className="nav-state current" aria-hidden="true" /> Now: <strong>{sdlc.current.navLabel}</strong>
                </>
              ) : (
                <>
                  <IconCheck width={12} height={12} strokeWidth={3} /> All phases complete
                </>
              )}
            </div>
          </>
        ) : (
          <div className="skeleton skeleton-line" />
        )
      ) : null}
    </div>
  );

  return (
    <>
      {mobileOpen && <div className="sidebar-scrim" onClick={closeMobile} />}
      <aside className={`sidebar${collapsed ? ' collapsed' : ''}${mobileOpen ? ' mobile-open' : ''}`} aria-label="Primary navigation">
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
          <button className="sidebar-mobile-close" onClick={closeMobile} aria-label="Close navigation">
            <IconX />
          </button>
        </div>

        <nav className="sidebar-scroll" aria-label="Main" onScroll={hideTip}>
          {renderGroup(NAV_GROUPS[0])}
          {projectCard}
          {NAV_GROUPS.slice(1).map(renderGroup)}
        </nav>

        <button className="sidebar-collapse-toggle" onClick={toggleCollapsed} aria-label={collapsed ? 'Expand navigation' : 'Collapse navigation'} title={collapsed ? 'Expand navigation' : 'Collapse navigation'}>
          <IconChevronLeft className={collapsed ? 'rotated' : undefined} />
        </button>
      </aside>
      {tip && rail && (
        <div className="nav-tooltip" role="tooltip" style={{ top: tip.top, left: tip.left }}>
          {tip.label}
        </div>
      )}
    </>
  );
}
