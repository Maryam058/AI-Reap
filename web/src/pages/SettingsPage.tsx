import { useEffect, useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { settingsApi } from '../api/settings';
import type { AiProviderStatus, SystemSettings } from '../api/types';
import { PageHeader } from '../components/ui/PageHeader';
import { PanelSkeleton } from '../components/ui/Skeletons';
import { ErrorState } from '../components/ui/ErrorState';
import { IconRefresh } from '../components/icons';
import { formatFullDateTime } from '../lib/format';

// Administrator-only, read-only view of GET /api/settings (SettingsController). Everything shown
// comes from server configuration; provider credentials are never sent to the browser.
export function SettingsPage() {
  const { token } = useAuth();
  const [settings, setSettings] = useState<SystemSettings | null>(null);
  const [failed, setFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setFailed(false);
    settingsApi
      .get(token)
      .then((data) => !cancelled && setSettings(data))
      .catch(() => !cancelled && setFailed(true));
    return () => {
      cancelled = true;
    };
  }, [token, attempt]);

  const header = (
    <PageHeader
      title="Settings"
      subtitle="AI configuration and system settings. Values come from server configuration and are read-only here."
      actions={
        <button type="button" className="btn btn-secondary" onClick={() => setAttempt((n) => n + 1)}>
          <IconRefresh width={16} height={16} /> Re-check
        </button>
      }
    />
  );

  if (failed) {
    return (
      <>
        {header}
        <ErrorState title="Unable to load settings" description="We couldn't load the current configuration right now." onRetry={() => setAttempt((n) => n + 1)} />
      </>
    );
  }

  if (!settings) {
    return (
      <>
        {header}
        <PanelSkeleton />
      </>
    );
  }

  const { ai, runtime } = settings;
  const primary = ai.status;
  const fallback = primary.fallback;

  return (
    <>
      {header}
      <section className="panel" aria-labelledby="ai-config-title">
        <h2 id="ai-config-title" className="panel-title">
          AI Configuration
        </h2>
        <p className="panel-sub">
          Checked {formatFullDateTime(ai.checkedAt)}.{' '}
          {ai.usingDevelopmentStub && 'No provider key is configured, so this environment is serving the development AI stub (canned data, not real AI).'}
        </p>
        <dl className="kv">
          <ProviderRows label="Primary" status={primary} />
          {ai.primaryCooldownUntil && (
            <div>
              <dt>Primary cool-down</dt>
              <dd>
                <span className="status-pill tone-warning">Rate-limited</span> requests go to the fallback until {formatFullDateTime(ai.primaryCooldownUntil)}
              </dd>
            </div>
          )}
          {fallback ? (
            <ProviderRows label="Fallback" status={fallback} />
          ) : (
            <div>
              <dt>Fallback provider</dt>
              <dd>Disabled</dd>
            </div>
          )}
        </dl>
      </section>

      <section className="panel" aria-labelledby="system-settings-title">
        <h2 id="system-settings-title" className="panel-title">
          System Settings
        </h2>
        <p className="panel-sub">Changed through server configuration; a restart applies them.</p>
        <dl className="kv">
          <div>
            <dt>Environment</dt>
            <dd>{runtime.environment}</dd>
          </div>
          <div>
            <dt>Automatic fallback</dt>
            <dd>{ai.fallbackEnabled ? 'Enabled' : 'Disabled'}</dd>
          </div>
          <div>
            <dt>Primary cool-down after quota exhaustion</dt>
            <dd>{runtime.primaryCooldownSeconds} seconds (maximum)</dd>
          </div>
          <div>
            <dt>AI request timeout</dt>
            <dd>{runtime.requestTimeoutSeconds} seconds per attempt</dd>
          </div>
          <div>
            <dt>AI request attempts</dt>
            <dd>{runtime.maxAttempts} (transient failures only)</dd>
          </div>
        </dl>
      </section>

      <section className="panel" aria-labelledby="security-title">
        <h2 id="security-title" className="panel-title">
          Security
        </h2>
        <p className="muted">{settings.credentialsNote}</p>
      </section>
    </>
  );
}

function YesNo({ value }: { value: boolean }) {
  return <span className={`status-pill tone-${value ? 'success' : 'danger'}`}>{value ? 'Yes' : 'No'}</span>;
}

function ProviderRows({ label, status }: { label: string; status: AiProviderStatus }) {
  return (
    <>
      <div>
        <dt>{label} provider</dt>
        <dd>{status.provider}</dd>
      </div>
      <div>
        <dt>{label} model</dt>
        <dd>
          <code>{status.model}</code>
        </dd>
      </div>
      <div>
        <dt>{label} configured</dt>
        <dd>
          <YesNo value={status.configured} />
        </dd>
      </div>
      <div>
        <dt>{label} model available</dt>
        <dd>
          <YesNo value={status.modelAvailable} />
        </dd>
      </div>
      {status.detail && (
        <div>
          <dt>{label} status</dt>
          <dd>{status.detail}</dd>
        </div>
      )}
    </>
  );
}
