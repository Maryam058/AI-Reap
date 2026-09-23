import { Navigate, Outlet, Route, Routes } from 'react-router-dom';
import { AuthProvider } from './auth/AuthContext';
import { ThemeProvider } from './context/ThemeContext';
import { SidebarProvider } from './context/SidebarContext';
import { ToastProvider } from './context/ToastContext';
import { ProtectedRoute } from './components/ProtectedRoute';
import { Layout } from './components/Layout';
import { LoginPage } from './pages/LoginPage';
import { RegisterPage } from './pages/RegisterPage';
import { DashboardPage } from './pages/DashboardPage';
import { ProjectsPage } from './pages/ProjectsPage';
import { ProjectGate } from './pages/ProjectGate';
import { ProjectOverviewPage } from './pages/ProjectOverviewPage';
import { EditProjectPage, NewProjectPage } from './pages/ProjectFormPages';
import { AnalysisPage, CopilotPage, DesignPage, DevelopmentPage, GatheringPage, TestingPage, TraceabilityPage, ValidationPage } from './pages/PhasePages';
import { UsersPage } from './pages/UsersPage';
import { SettingsPage } from './pages/SettingsPage';
import { RolesPage } from './pages/RolesPage';
import { AuditLogsPage } from './pages/AuditLogsPage';

/** Authenticated application frame. Mounted once so the sidebar and loaded project persist across navigation. */
function AppShell() {
  return (
    <ProtectedRoute>
      <Layout>
        <Outlet />
      </Layout>
    </ProtectedRoute>
  );
}

function App() {
  return (
    <ThemeProvider>
      <AuthProvider>
        <SidebarProvider>
          <ToastProvider>
            <Routes>
              <Route path="/login" element={<LoginPage />} />
              <Route path="/register" element={<RegisterPage />} />

              <Route element={<AppShell />}>
                <Route path="/dashboard" element={<DashboardPage />} />
                <Route path="/projects" element={<ProjectsPage />} />
                <Route path="/projects/new" element={<NewProjectPage />} />
                <Route path="/projects/:id" element={<ProjectGate />}>
                  <Route index element={<ProjectOverviewPage />} />
                  <Route path="edit" element={<EditProjectPage />} />
                  <Route path="gathering" element={<GatheringPage />} />
                  <Route path="analysis" element={<AnalysisPage />} />
                  <Route path="validation" element={<ValidationPage />} />
                  <Route path="design" element={<DesignPage />} />
                  <Route path="development" element={<DevelopmentPage />} />
                  <Route path="testing" element={<TestingPage />} />
                  <Route path="traceability" element={<TraceabilityPage />} />
                  <Route path="copilot" element={<CopilotPage />} />
                  <Route path="*" element={<Navigate to=".." replace />} />
                </Route>
                <Route
                  path="/admin/users"
                  element={
                    <ProtectedRoute role="Administrator">
                      <UsersPage />
                    </ProtectedRoute>
                  }
                />
                <Route
                  path="/admin/roles"
                  element={
                    <ProtectedRoute role="Administrator">
                      <RolesPage />
                    </ProtectedRoute>
                  }
                />
                <Route
                  path="/admin/audit"
                  element={
                    <ProtectedRoute role="Administrator">
                      <AuditLogsPage />
                    </ProtectedRoute>
                  }
                />
                <Route path="/settings" element={<SettingsPage />} />
              </Route>

              <Route path="*" element={<Navigate to="/dashboard" replace />} />
            </Routes>
          </ToastProvider>
        </SidebarProvider>
      </AuthProvider>
    </ThemeProvider>
  );
}

export default App;
