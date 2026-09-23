import type { ReactNode } from 'react';
import { IconAlert } from '../icons';

/** Friendly error panel. Never receives or renders raw server payloads. */
export function ErrorState({
  title,
  description,
  onRetry,
  action,
}: {
  title: string;
  description: string;
  onRetry?: () => void;
  action?: ReactNode;
}) {
  return (
    <div className="error-state" role="alert">
      <div className="error-state-icon">
        <IconAlert width={22} height={22} />
      </div>
      <h3>{title}</h3>
      <p>{description}</p>
      <div className="error-state-actions">
        {onRetry && (
          <button type="button" className="btn btn-primary" onClick={onRetry}>
            Try again
          </button>
        )}
        {action}
      </div>
    </div>
  );
}
