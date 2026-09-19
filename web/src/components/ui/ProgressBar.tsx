export function ProgressBar({ percent }: { percent: number }) {
  return (
    <div className="progress-track">
      <div className="progress-fill" style={{ width: `${Math.min(Math.max(percent, 2), 100)}%` }} />
    </div>
  );
}
