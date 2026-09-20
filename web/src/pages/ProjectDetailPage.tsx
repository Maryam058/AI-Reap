import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { apiClient, ApiError } from '../api/client';
import type { Project } from '../api/types';
import { Layout } from '../components/Layout';
import { PageHeader } from '../components/ui/PageHeader';
import { RequirementWorkspace } from '../components/RequirementWorkspace';
import { DashboardPanel } from '../components/DashboardPanel';
import { AgentPipelinePanel } from '../components/AgentPipelinePanel';
import { ConflictsPanel } from '../components/ConflictsPanel';
import { TraceabilityMatrixPanel } from '../components/TraceabilityMatrixPanel';
import { AuditTrailPanel } from '../components/AuditTrailPanel';
import { DocumentsPanel } from '../components/DocumentsPanel';
import { CopilotPanel } from '../components/CopilotPanel';
import { ProjectSettingsPanel } from '../components/ProjectSettingsPanel';
import { statusLabel, statusVariant } from '../lib/format';

export function ProjectDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { token } = useAuth();
  const [project, setProject] = useState<Project | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);

  useEffect(() => {
    if (!id) return;
    apiClient
      .get<Project>(`/api/projects/${id}`, token)
      .then(setProject)
      .catch((err) => setError(err instanceof ApiError ? err.message : 'Failed to load project.'));
  }, [id, token]);

  if (!project) {
    return <Layout>{error ? <p className="error">{error}</p> : <p>Loading…</p>}</Layout>;
  }

  return (
    <Layout>
      <PageHeader
        title={project.name}
        subtitle={project.description}
        actions={
          <span className={`status-pill tone-${statusVariant(project.status)}`}>
            {statusLabel(project.status)}
          </span>
        }
      />

      {error && <p className="error">{error}</p>}

      <ProjectSettingsPanel project={project} onProjectUpdated={setProject} />
      <DashboardPanel projectId={project.id} refreshKey={refreshKey} />
      <RequirementWorkspace projectId={project.id} onChange={() => setRefreshKey((k) => k + 1)} />
      <AgentPipelinePanel projectId={project.id} refreshKey={refreshKey} onChange={() => setRefreshKey((k) => k + 1)} />
      <ConflictsPanel projectId={project.id} />
      <TraceabilityMatrixPanel projectId={project.id} />
      <DocumentsPanel projectId={project.id} />
      <CopilotPanel projectId={project.id} />
      <AuditTrailPanel projectId={project.id} />
    </Layout>
  );
}
