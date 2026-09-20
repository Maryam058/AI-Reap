import type { ReactNode } from 'react';
import { Navigate, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { Layout } from './Layout';

export function ProtectedRoute({ children, role }: { children: ReactNode; role?: string }) {
  const { token, roles, hasRole, logout } = useAuth();
  const navigate = useNavigate();

  if (!token) {
    return <Navigate to="/login" replace />;
  }

  // Signed up but not yet given a role: the API refuses everything for this account, so say why.
  if (roles.length === 0) {
    return (
      <Layout>
        <div className="card">
          <h2>Waiting for a role</h2>
          <p>
            Your account has been created but has no role yet, so you cannot open projects. Ask an administrator to
            assign you one, then sign in again.
          </p>
          <button
            type="button"
            onClick={() => {
              logout();
              navigate('/login');
            }}
          >
            Sign out
          </button>
        </div>
      </Layout>
    );
  }

  // UI convenience only — the backend enforces the role on every request.
  if (role && !hasRole(role)) {
    return <Navigate to="/projects" replace />;
  }

  return <>{children}</>;
}
