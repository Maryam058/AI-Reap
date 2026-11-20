import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import type { BlockerGroup, CheckGuidance as Guidance } from '../api/types';
import { personLabel, useMemberNames } from '../hooks/useMemberNames';
import { destinationLabel, destinationPath, itemPath, kindLabel, primaryRoles, rolesText } from '../lib/checkGuidance';
import { IconAlert, IconArrowRight, IconCheck, IconLock, IconUsers } from './icons';

const VISIBLE_ITEMS = 8;

/**
 * "How to meet this" for one failing exit check (ExitCheckGuidanceService): what blocks it, grouped
 * by what each blocker needs, who can resolve it - the viewer, or which role/member - and a link to
 * the existing screen where that is done. Purely navigational; every action stays on its own screen
 * under the backend's rules.
 */
export function CheckGuidance({
  projectId,
  guidance,
  canOverride,
  labelFor,
}: {
  projectId: string;
  guidance: Guidance;
  /** The viewer is an Administrator who may override failing checks with a reason. */
  canOverride?: boolean;
  /** Label of another check, by key (for "follows from"). */
  labelFor?: (key: string) => string | undefined;
}) {
  const anyFixable = guidance.blockers.some((g) => g.callerCanFix);
  const follows = guidance.dependsOn.map((k) => labelFor?.(k)).filter((l): l is string => !!l);

  return (
    <div className="cg">
      <p className="cg-howto">
        <strong>How to meet this:</strong> {guidance.howTo}
      </p>
      {guidance.kind === 'automatic' && (
        <p className="cg-auto">
          <span className="cg-kind kind-automatic">Automatic</span>
          Nobody marks this one: it passes by itself once the work below is done{follows.length > 0 && <> (see {follows.join(', ')})</>}.
        </p>
      )}

      {guidance.blockers.map((group, i) => (
        <BlockerGroupView key={group.reason} projectId={projectId} group={group} step={guidance.blockers.length > 1 ? i + 1 : null} />
      ))}

      {canOverride && !anyFixable && (
        <p className="cg-note">
          <IconLock width={12} height={12} /> As an Administrator you can still move the stage as an override with a reason; it is recorded with the failing checks.
        </p>
      )}
    </div>
  );
}

function BlockerGroupView({ projectId, group, step }: { projectId: string; group: BlockerGroup; step: number | null }) {
  const { userId, hasRole } = useAuth();
  const names = useMemberNames(projectId);
  const [showAll, setShowAll] = useState(false);
  const to = destinationPath(projectId, group.destination);
  const where = destinationLabel(group.destination);
  const needed = rolesText(primaryRoles(group.fixRoles));
  const shown = showAll ? group.items : group.items.slice(0, VISIBLE_ITEMS);
  const listed = group.items.length + group.hiddenCount;
  const count = group.totalCount;
  const canStaff = hasRole('Administrator') || hasRole('BusinessAnalyst');

  return (
    <section className={`cg-group${group.callerCanFix ? ' is-mine' : ''}`} aria-label={group.title}>
      <div className="cg-group-head">
        <span className="cg-title">
          {step !== null && <span className="cg-step">{step}</span>}
          <strong>{group.title}</strong>
          {count > 0 && <span className="muted"> · {count}</span>}
        </span>
        <span className={`cg-kind kind-${group.kind}`}>{kindLabel(group.kind)}</span>
        {group.callerCanFix ? (
          <span className="status-pill tone-success">
            <IconCheck width={11} height={11} /> You can do this
          </span>
        ) : (
          <span className="status-pill tone-neutral">{group.kind === 'approval' ? `Needs approval by a ${needed}` : group.kind === 'signoff' ? `Sign-off by ${needed}` : `Needs a ${needed}`}</span>
        )}
      </div>

      <p className="cg-instruction">{group.instruction}</p>

      {!group.callerCanFix && (
        <p className="cg-who">
          <IconUsers width={12} height={12} />
          {group.assignees.length > 0 ? (
            <span>
              Who can do it: {group.assignees.map((id) => personLabel(id, null, names, userId)).join(', ')}
              {` (${needed})`}
            </span>
          ) : (
            <span>
              No member of this project holds the {needed} role.{' '}
              {canStaff ? (
                <Link to={destinationPath(projectId, { area: 'members' })!}>Add one in Members</Link>
              ) : (
                'Ask a Business Analyst or Administrator to add one to the project.'
              )}
            </span>
          )}
        </p>
      )}

      {group.callerNote && (
        <p className="cg-note">
          <IconAlert width={12} height={12} /> {group.callerNote}
        </p>
      )}

      {listed > 0 && (
        <div className="cg-items">
          {shown.map((item) => (
            <Link key={item.artifactId} to={itemPath(projectId, item)} className="cg-item" title={item.title}>
              <span className="code">{item.code}</span>
              {item.note && <span className="cg-item-note">{item.note}</span>}
            </Link>
          ))}
          {group.items.length > VISIBLE_ITEMS && (
            <button type="button" className="btn btn-ghost btn-sm cg-more" onClick={() => setShowAll((v) => !v)} aria-expanded={showAll}>
              {showAll ? 'Show fewer' : `Show all ${group.items.length}`}
            </button>
          )}
          {group.hiddenCount > 0 && (
            <span className="cg-hidden muted">
              {group.items.length > 0 ? '+ ' : ''}
              {group.hiddenCount} {group.hiddenCount === 1 ? 'item' : 'items'} you can't view until approved
            </span>
          )}
          {count > listed && <span className="cg-hidden muted">Showing the first {group.items.length} of {count}.</span>}
        </div>
      )}

      {to ? (
        <div className="cg-go">
          <Link to={to} className={`btn btn-sm ${group.callerCanFix ? 'btn-primary' : 'btn-secondary'}`}>
            {group.callerCanFix ? `Go to ${where}` : `View ${where}`} <IconArrowRight width={13} height={13} />
          </Link>
        </div>
      ) : (
        group.destination.area === 'lifecycle' && <p className="muted cg-go">Done from this lifecycle panel, with the submit button below.</p>
      )}
    </section>
  );
}
