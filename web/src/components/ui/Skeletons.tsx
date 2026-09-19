export function MetricGridSkeleton() {
  return (
    <div className="skeleton-metric-grid">
      {Array.from({ length: 4 }).map((_, i) => (
        <div key={i} className="skeleton skeleton-metric-card" />
      ))}
    </div>
  );
}

export function ProjectGridSkeleton({ count = 3 }: { count?: number }) {
  return (
    <div className="skeleton-project-grid">
      {Array.from({ length: count }).map((_, i) => (
        <div key={i} className="skeleton skeleton-project-card" />
      ))}
    </div>
  );
}

export function PanelSkeleton() {
  return <div className="skeleton skeleton-panel" />;
}
