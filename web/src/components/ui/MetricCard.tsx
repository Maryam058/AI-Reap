import type { ReactNode } from 'react';

export function MetricCard({
  icon,
  tone,
  value,
  label,
  trend,
}: {
  icon: ReactNode;
  tone: 'primary' | 'success' | 'warning' | 'ai';
  value: ReactNode;
  label: string;
  trend?: string;
}) {
  return (
    <div className="metric-card">
      <div className="metric-card-top">
        <div className={`metric-icon tone-${tone}`}>{icon}</div>
        {trend && <span className="metric-trend">{trend}</span>}
      </div>
      <div className="metric-value">{value}</div>
      <div className="metric-label">{label}</div>
    </div>
  );
}
