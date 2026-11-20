import type { StageHandoff } from '../lib/stageHandoff';
import { roleLabel } from '../lib/lifecycle';
import { Dialog } from './ui/Dialog';
import { IconAlert, IconCheckCircle, IconUser } from './icons';

/**
 * Shown once a lifecycle move has really happened (the backend completed it): which stage is done,
 * which stage the project is in now, and which role picks it up - and so whether the current user
 * carries on or another user has to sign in. It never signs anyone out or moves the project.
 */
export function StageHandoffDialog({ handoff, onClose }: { handoff: StageHandoff; onClose: () => void }) {
  const complete = handoff.access === 'complete';
  return (
    <Dialog
      title={handoff.title}
      onClose={onClose}
      width={520}
      footer={
        <button type="button" className="btn btn-primary" onClick={onClose} data-autofocus>
          Continue
        </button>
      }
    >
      <div className="alert alert-success" role="status">
        <IconCheckCircle width={16} height={16} />
        <span>
          <strong>{handoff.completedStage}</strong> is complete{complete ? ' and the project lifecycle is finished.' : '.'}
        </span>
      </div>

      <dl className="kv lc-kv">
        <div>
          <dt>Completed stage</dt>
          <dd>{handoff.completedStage}</dd>
        </div>
        <div>
          <dt>{complete ? 'Project stage' : 'Next stage'}</dt>
          <dd>{handoff.nextStage}</dd>
        </div>
        {handoff.nextRole && (
          <div>
            <dt>Required role</dt>
            <dd>{roleLabel(handoff.nextRole)}</dd>
          </div>
        )}
      </dl>

      <p className="dialog-text">
        <strong>{handoff.message}</strong>
      </p>

      {handoff.access === 'continue' || complete ? (
        <p className="dialog-text">{handoff.instruction}</p>
      ) : (
        <div className={`alert ${handoff.access === 'administrator' ? 'alert-warning' : 'alert-info'}`}>
          {handoff.access === 'administrator' ? <IconAlert width={16} height={16} /> : <IconUser width={16} height={16} />}
          <span>{handoff.instruction} You can sign out from the top bar; you won't be signed out automatically.</span>
        </div>
      )}

      {handoff.gateNote && <p className="dialog-text muted">{handoff.gateNote}</p>}
    </Dialog>
  );
}
