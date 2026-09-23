import { useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { Link, matchPath, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { useTheme } from '../context/ThemeContext';
import { useSidebar } from '../context/SidebarContext';
import { ProjectProvider, useProjectContext } from '../context/ProjectContext';
import { SDLC_PHASES } from '../lib/sdlc';
import { Sidebar } from './Sidebar';
import { Breadcrumbs, type Crumb } from './Breadcrumbs';
import { Avatar } from './ui/Avatar';
import { IconFolder, IconLogout, IconMenu, IconMoon, IconSearch, IconSun } from './icons';

const SEGMENT_GROUP: Record<string, string> = {
  gathering: 'Project',
  design: 'SDLC',
  development: 'SDLC',
  testing: 'SDLC',
  analysis: 'SDLC',
  validation: 'AI & Automation',
  traceability: 'AI & Automation',
  copilot: 'AI & Automation',
};

function useCrumbs(pathname: string, projectName: string | undefined): Crumb[] {
  const m = matchPath({ path: '/projects/:id/*', end: true }, pathname);
  const id = m?.params.id && m.params.id !== 'new' ? m.params.id : null;
  if (id) {
    const segment = (m?.params['*'] ?? '').split('/')[0];
    const base: Crumb[] = [{ label: 'Projects', to: '/projects' }];
    if (!segment) return [...base, { label: projectName ?? 'Project' }];
    const project: Crumb = { label: projectName ?? 'Project', to: `/projects/${id}` };
    if (segment === 'edit') return [...base, project, { label: 'Edit' }];
    const phase = SDLC_PHASES.find((p) => p.segment === segment);
    const label = phase ? phase.navLabel.replace(/^Requirements /, '') : segment === 'copilot' ? 'AI Assistant' : segment;
    return [...base, project, { label: SEGMENT_GROUP[segment] ?? 'Project' }, { label }];
  }
  if (pathname === '/projects/new') return [{ label: 'Projects', to: '/projects' }, { label: 'New Project' }];
  if (pathname.startsWith('/projects')) return [{ label: 'Workspace' }, { label: 'Projects' }];
  if (pathname.startsWith('/dashboard')) return [{ label: 'Workspace' }, { label: 'Dashboard' }];
  if (pathname.startsWith('/admin/users')) return [{ label: 'Management' }, { label: 'Users' }];
  if (pathname.startsWith('/admin/roles')) return [{ label: 'Management' }, { label: 'Users', to: '/admin/users' }, { label: 'Roles & Permissions' }];
  if (pathname.startsWith('/admin/audit')) return [{ label: 'Management' }, { label: 'Users', to: '/admin/users' }, { label: 'Audit Logs' }];
  if (pathname.startsWith('/settings')) return [{ label: 'Management' }, { label: 'Settings' }];
  return [];
}

function GlobalSearch() {
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const { pathname } = useLocation();
  const [value, setValue] = useState(pathname === '/projects' ? (params.get('q') ?? '') : '');
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const el = e.target as HTMLElement;
      if (e.key === '/' && !/^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName) && !el.isContentEditable) {
        e.preventDefault();
        input.current?.focus();
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, []);

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    const q = value.trim();
    navigate(q ? `/projects?q=${encodeURIComponent(q)}` : '/projects');
  };

  return (
    <form className="topbar-search" role="search" onSubmit={onSubmit}>
      <IconSearch width={15} height={15} />
      <input ref={input} type="search" value={value} onChange={(e) => setValue(e.target.value)} placeholder="Search projects…" aria-label="Search projects" />
      <kbd aria-hidden="true">/</kbd>
    </form>
  );
}

function Shell({ children }: { children: ReactNode }) {
  const { displayName, roles, logout } = useAuth();
  const { theme, toggleTheme } = useTheme();
  const { openMobile } = useSidebar();
  const navigate = useNavigate();
  const { pathname } = useLocation();
  const { project, projectId } = useProjectContext();
  const crumbs = useCrumbs(pathname, project?.name);

  return (
    <div className="app-shell">
      <Sidebar />
      <div className="app-content">
        <header className="topbar">
          <button className="icon-btn sidebar-open-btn" onClick={openMobile} aria-label="Open navigation">
            <IconMenu />
          </button>
          {projectId && (
            <Link to={`/projects/${projectId}`} className="topbar-project" title={project?.name}>
              <IconFolder width={14} height={14} />
              <span className="topbar-project-label">Project</span>
              <strong>{project?.name ?? '…'}</strong>
            </Link>
          )}
          <div className="topbar-spacer">
            <Breadcrumbs crumbs={crumbs} />
          </div>
          <GlobalSearch />
          <div className="topbar-actions">
            <button
              className="icon-btn"
              onClick={toggleTheme}
              aria-label={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
              title={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
            >
              {theme === 'dark' ? <IconSun /> : <IconMoon />}
            </button>
            <div className="topbar-user">
              <Avatar name={displayName} />
              <div className="topbar-user-info">
                <span className="topbar-user-name">{displayName}</span>
                <span className="roles">{roles.join(', ')}</span>
              </div>
              <button
                className="icon-btn"
                onClick={() => {
                  logout();
                  navigate('/login');
                }}
                title="Sign out"
                aria-label="Sign out"
              >
                <IconLogout />
              </button>
            </div>
          </div>
        </header>
        <main id="main">{children}</main>
      </div>
    </div>
  );
}

export function Layout({ children }: { children: ReactNode }) {
  const { pathname } = useLocation();
  const m = matchPath({ path: '/projects/:id/*', end: true }, pathname);
  const id = m?.params.id && m.params.id !== 'new' ? m.params.id : null;
  return (
    <ProjectProvider projectId={id}>
      <a href="#main" className="skip-link">
        Skip to content
      </a>
      <Shell>{children}</Shell>
    </ProjectProvider>
  );
}
