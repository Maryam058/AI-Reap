import { useEffect, useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { dashboardApi } from '../api/dashboard';
import type { ProjectDashboard } from '../api/types';

function Stat({ label, value }: { label: string; value: number }) {
  return (
    <div className="stat-tile">
      <div className="stat-value">{value}</div>
      <div className="stat-label">{label}</div>
    </div>
  );
}

export function DashboardPanel({ projectId, refreshKey }: { projectId: string; refreshKey: number }) {
  const { token } = useAuth();
  const [data, setData] = useState<ProjectDashboard | null>(null);

  useEffect(() => {
    dashboardApi.get(projectId, token).then(setData).catch(() => setData(null));
  }, [projectId, token, refreshKey]);

  if (!data) return null;

  return (
    <div className="card dashboard-panel">
      <h2>Dashboard</h2>
      <div className="stat-grid">
        <Stat label="Functional reqs" value={data.functionalRequirementCount} />
        <Stat label="Non-functional reqs" value={data.nonFunctionalRequirementCount} />
        <Stat label="User stories" value={data.userStoryCount} />
        <Stat label="Acceptance criteria" value={data.acceptanceCriterionCount} />
        <Stat label="Open questions" value={data.openClarificationCount} />
        <Stat label="Approved" value={data.approvedCount} />
        <Stat label="Pending review" value={data.pendingReviewCount} />
        <Stat label="Rejected" value={data.rejectedCount} />
      </div>
      {data.recentChanges.length > 0 && (
        <>
          <h3>Recent changes</h3>
          <ul className="recent-changes">
            {data.recentChanges.map((c, i) => (
              <li key={i}>
                <span className="code">{c.code}</span> {c.title}
                <span className="status-pill">{c.status}</span>
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  );
}
