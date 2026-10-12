import type { StageHandoff } from '../lib/stageHandoff';
import { missingMemberMessage } from '../lib/stageHandoff';
import { roleLabel } from '../lib/lifecycle';
import { roleName } from '../lib/roleGuard';
import { Dialog } from './ui/Dialog';
import { IconAlert, IconCheckCircle, IconUsers } from './icons';

/**
 * Shown instead of the hand-off when the project has no member who can pick up the next stage
 * ('missing'), or when the member list couldn't be loaded to find out ('error'). It sends the user
 * to the existing member management; it never moves the project or grants anything itself.
 */
export function NextRoleMemberDialog({
  handoff,
  problem,
  canManage,
  checking,
  onManage,
  onRecheck,
  onClose,
}: {
  handoff: StageHandoff;
  problem: 'missing' | 'error';
  /** Whether the user may add members (Business Analyst or Administrator); the backend enforces it. */
  canManage: boolean;
  checking: boolean;
  onManage: () => void;
  onRecheck: () => void;
  onClose: () => void;
}) {
  const role = handoff.nextRole ?? '';
  const missing = problem === 'missing';
  return (
    <Dialog
      title={missing ? 'Next Stage Requires a Project Member' : 'Could Not Verify Project Members'}
      onClose={onClose}
      width={520}
      footer={
        <>
          <button type="button" className="btn btn-secondary" onClick={onClose}>
            Close
          </button>
          <button type="button" className="btn btn-secondary" onClick={onRecheck} disabled={checking}>
            {checking ? 'Checking…' : 'Check again'}
          </button>
          {missing && (
            <button type="button" className="btn btn-primary" onClick={onManage} data-autofocus>
              <IconUsers width={16} height={16} /> Manage Project Members
            </button>
          )}
        </>
      }
    >
      <div className="alert alert-success" role="status">
        <IconCheckCircle width={16} height={16} />
        <span>
          <strong>{handoff.completedStage}</strong> is complete.
        </span>
      </div>

      <dl className="kv lc-kv">
        <div>
          <dt>Completed stage</dt>
          <dd>{handoff.completedStage}</dd>
        </div>
        <div>
          <dt>Next stage</dt>
          <dd>{handoff.nextStage}</dd>
        </div>
        <div>
          <dt>Required role</dt>
          <dd>{roleLabel(role)}</dd>
        </div>
      </dl>

      {missing ? (
        <>
          <div className="alert alert-warning">
            <IconAlert width={16} height={16} />
            <span>{missingMemberMessage(role)}</span>
          </div>
          <p className="dialog-text muted">
            {canManage
              ? `Add an active ${roleName(role)} by email under Members, then come back to this overview; the project is checked again and nothing moves on automatically.`
              : `Only a Business Analyst or an Administrator can add project members. Ask one of them to add an active ${roleName(role)}.`}
          </p>
        </>
      ) : (
        <div className="alert alert-danger" role="alert">
          <IconAlert width={16} height={16} />
          <span>
            The project's member list could not be loaded, so we couldn't check whether a {roleName(role)} is assigned. This does not mean nobody is. Try
            again in a moment.
          </span>
        </div>
      )}
    </Dialog>
  );
}
