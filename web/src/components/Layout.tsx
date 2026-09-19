import type { ReactNode } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { useTheme } from '../context/ThemeContext';
import { useSidebar } from '../context/SidebarContext';
import { Sidebar } from './Sidebar';
import { Avatar } from './ui/Avatar';
import { IconLogout, IconMenu, IconMoon, IconSun } from './icons';

export function Layout({ children }: { children: ReactNode }) {
  const { displayName, roles, logout } = useAuth();
  const { theme, toggleTheme } = useTheme();
  const { openMobile } = useSidebar();
  const navigate = useNavigate();

  return (
    <div className="app-shell">
      <Sidebar />
      <div className="app-content">
        <header className="topbar">
          <button className="icon-btn sidebar-open-btn" onClick={openMobile} aria-label="Open navigation">
            <IconMenu />
          </button>
          <div className="topbar-spacer" />
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
        <main>{children}</main>
      </div>
    </div>
  );
}
