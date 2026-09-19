import { useEffect } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { useSidebar } from '../context/SidebarContext';
import { IconChat, IconChevronLeft, IconDocument, IconGrid, IconLink, IconSettings, IconX } from './icons';

const workspaceItems = [
  { label: 'Requirements', icon: IconDocument },
  { label: 'Traceability', icon: IconLink },
  { label: 'Copilot', icon: IconChat },
  { label: 'Settings', icon: IconSettings },
];

export function Sidebar() {
  const location = useLocation();
  const { collapsed, toggleCollapsed, mobileOpen, closeMobile } = useSidebar();
  const isDashboard = location.pathname === '/projects';

  useEffect(() => {
    closeMobile();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [location.pathname]);

  return (
    <>
      {mobileOpen && <div className="sidebar-scrim" onClick={closeMobile} />}
      <aside className={`sidebar${collapsed ? ' collapsed' : ''}${mobileOpen ? ' mobile-open' : ''}`}>
        <div className="sidebar-brand-row">
          <Link to="/projects" className="sidebar-brand" title={collapsed ? 'AI-REAP' : undefined}>
            <span className="sidebar-logo">AI</span>
            {!collapsed && (
              <span>
                <span className="sidebar-brand-name">AI-REAP</span>
                <br />
                <span className="sidebar-brand-tag">SDLC Automation</span>
              </span>
            )}
          </Link>
          <button className="sidebar-mobile-close" onClick={closeMobile} aria-label="Close navigation">
            <IconX />
          </button>
        </div>

        <nav className="sidebar-nav">
          <Link
            to="/projects"
            className={`sidebar-link${isDashboard ? ' active' : ''}`}
            title={collapsed ? 'Dashboard' : undefined}
          >
            <IconGrid />
            {!collapsed && 'Dashboard'}
          </Link>
        </nav>

        {!collapsed && <div className="sidebar-section-label">Workspace</div>}
        <nav className="sidebar-nav">
          {workspaceItems.map(({ label, icon: Icon }) => (
            <div className="sidebar-link disabled" key={label} aria-disabled="true" title={collapsed ? label : undefined}>
              <Icon />
              {!collapsed && (
                <>
                  {label}
                  <span className="sidebar-badge">Soon</span>
                </>
              )}
            </div>
          ))}
        </nav>

        {!collapsed && (
          <div className="sidebar-footer">
            <div className="sidebar-hint">
              Requirements, traceability, and Copilot are available today from within each project.
            </div>
          </div>
        )}

        <button
          className="sidebar-collapse-toggle"
          onClick={toggleCollapsed}
          aria-label={collapsed ? 'Expand navigation' : 'Collapse navigation'}
          title={collapsed ? 'Expand navigation' : 'Collapse navigation'}
        >
          <IconChevronLeft className={collapsed ? 'rotated' : undefined} />
        </button>
      </aside>
    </>
  );
}
