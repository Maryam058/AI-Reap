import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { notificationsApi } from '../api/notifications';
import { NOTIFICATION_KIND, type ProjectNotification } from '../api/types';
import { formatRelativeTime } from '../lib/format';
import { useToast } from '../context/ToastContext';
import { apiErrorText } from '../lib/errors';
import { IconBell, IconCheck } from './icons';

const POLL_MS = 60_000;

const KIND: Record<number, { label: string; tone: string }> = {
  [NOTIFICATION_KIND.Handover]: { label: 'Stage handover', tone: 'info' },
  [NOTIFICATION_KIND.ApprovalRequested]: { label: 'Approval requested', tone: 'warning' },
  [NOTIFICATION_KIND.TransitionRejected]: { label: 'Request rejected', tone: 'danger' },
};

/**
 * Lifecycle notifications in the top bar: handovers to a stage's owners (the person who moved the
 * project included, when they own the new stage), approval requests to Reviewers, and rejections
 * to the requester. Shown exactly as the API returns them - nothing is filtered out here.
 */
export function NotificationBell() {
  const { token } = useAuth();
  const { toast } = useToast();
  const [open, setOpen] = useState(false);
  const [showAll, setShowAll] = useState(false);
  const [items, setItems] = useState<ProjectNotification[] | null>(null);
  const [unread, setUnread] = useState(0);
  const [refresh, setRefresh] = useState(0);
  const root = useRef<HTMLDivElement>(null);
  const reload = () => setRefresh((n) => n + 1);

  // Polls while mounted; `refresh` forces an immediate re-read (on open, after acknowledging).
  useEffect(() => {
    let cancelled = false;
    const fetchList = () =>
      notificationsApi
        .list(showAll, token)
        .then((list) => {
          if (cancelled) return;
          setItems(list);
          setUnread(showAll ? list.filter((n) => !n.acknowledgedAt).length : list.length);
        })
        .catch(() => undefined); // best effort: the bell simply shows nothing new
    void fetchList();
    const timer = window.setInterval(fetchList, POLL_MS);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [showAll, token, refresh]);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => !root.current?.contains(e.target as Node) && setOpen(false);
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false);
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const acknowledge = async (ids: string[]) => {
    try {
      await Promise.all(ids.map((id) => notificationsApi.acknowledge(id, token)));
      reload();
    } catch (err) {
      toast(apiErrorText(err, 'Could not mark the notification as read.'), 'error');
    }
  };

  const open_ = (items ?? []).filter((n) => !n.acknowledgedAt);

  return (
    <div className="notif" ref={root}>
      <button
        type="button"
        className="icon-btn notif-btn"
        aria-label={unread > 0 ? `Notifications, ${unread} unread` : 'Notifications'}
        aria-expanded={open}
        onClick={() => {
          setOpen((o) => !o);
          reload();
        }}
      >
        <IconBell />
        {unread > 0 && <span className="notif-count">{unread > 99 ? '99+' : unread}</span>}
      </button>

      {open && (
        <div className="notif-panel" role="dialog" aria-label="Notifications">
          <div className="notif-head">
            <strong>Notifications</strong>
            <label className="notif-toggle">
              <input type="checkbox" checked={showAll} onChange={(e) => setShowAll(e.target.checked)} /> Show read
            </label>
            {open_.length > 1 && (
              <button type="button" className="btn btn-ghost btn-sm" onClick={() => acknowledge(open_.map((n) => n.id))}>
                Mark all read
              </button>
            )}
          </div>
          {items === null ? (
            <div className="skeleton skeleton-line" />
          ) : items.length === 0 ? (
            <p className="notif-empty">{showAll ? 'No notifications yet.' : 'You are all caught up.'}</p>
          ) : (
            <ul className="notif-list">
              {items.map((n) => {
                const kind = KIND[n.kind] ?? { label: 'Lifecycle', tone: 'neutral' };
                return (
                  <li key={n.id} className={n.acknowledgedAt ? 'read' : 'unread'}>
                    <div className="notif-meta">
                      <span className={`status-pill tone-${kind.tone}`}>{kind.label}</span>
                      <span className="muted">{formatRelativeTime(n.createdAt)}</span>
                    </div>
                    <Link to={`/projects/${n.projectId}#lifecycle`} className="notif-message" onClick={() => setOpen(false)}>
                      {n.message}
                    </Link>
                    <div className="notif-foot">
                      <span className="muted">{n.projectName}</span>
                      {n.acknowledgedAt ? (
                        <span className="muted">
                          <IconCheck width={12} height={12} /> Read
                        </span>
                      ) : (
                        <button type="button" className="btn btn-ghost btn-sm" onClick={() => acknowledge([n.id])}>
                          Mark read
                        </button>
                      )}
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
        </div>
      )}
    </div>
  );
}
