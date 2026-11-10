import { useEffect, useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { aiUsageApi } from '../api/aiUsage';
import type { AiUsageSnapshot } from '../api/types';
import { PageHeader } from '../components/ui/PageHeader';
import { PanelSkeleton } from '../components/ui/Skeletons';
import { ErrorState } from '../components/ui/ErrorState';
import { EmptyState } from '../components/ui/EmptyState';
import { IconBarChart, IconRefresh } from '../components/icons';
import { formatCallTime, formatTokens, isEmptyUsage, modelsByTokens } from '../lib/aiUsage';

// Administrator-only, read-only view of GET /api/ai/usage (AiUsageMeter): the token usage the AI provider
// reported for each response since this API instance started. Kept in server memory only, so it restarts
// at zero; the provider's own console remains the authority for billing.
export function AiUsagePage() {
  const { token } = useAuth();
  const [usage, setUsage] = useState<AiUsageSnapshot | null>(null);
  const [failed, setFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);
  // A retry clears the failure here, in the event, rather than in the effect.
  const retry = () => {
    setFailed(false);
    setAttempt((n) => n + 1);
  };

  useEffect(() => {
    let cancelled = false;
    aiUsageApi
      .get(token)
      .then((data) => !cancelled && setUsage(data))
      .catch(() => !cancelled && setFailed(true));
    return () => {
      cancelled = true;
    };
  }, [token, attempt]);

  const header = (
    <PageHeader
      title="AI Usage"
      subtitle="Tokens the AI provider reported since the API last started. Counted in memory only: a restart starts again at zero, and the provider's console is the authority for billing."
      actions={
        <button type="button" className="btn btn-secondary" onClick={retry}>
          <IconRefresh width={16} height={16} /> Refresh
        </button>
      }
    />
  );

  if (failed) {
    return (
      <>
        {header}
        <ErrorState title="Unable to load AI usage" description="We couldn't load usage figures right now." onRetry={retry} />
      </>
    );
  }

  if (!usage) {
    return (
      <>
        {header}
        <PanelSkeleton />
      </>
    );
  }

  const { totals } = usage;
  return (
    <>
      {header}
      <section className="panel" aria-labelledby="ai-usage-totals">
        <h2 id="ai-usage-totals" className="panel-title">
          Totals
        </h2>
        <p className="panel-sub">Since {formatCallTime(usage.since)}. Every billed response counts, including retries and responses that were rejected as incomplete.</p>
        <dl className="kv">
          <div>
            <dt>AI calls</dt>
            <dd>{formatTokens(totals.requests)}</dd>
          </div>
          <div>
            <dt>Input tokens</dt>
            <dd>{formatTokens(totals.inputTokens)}</dd>
          </div>
          <div>
            <dt>Output tokens</dt>
            <dd>{formatTokens(totals.outputTokens)}</dd>
          </div>
          <div>
            <dt>Cache reads / writes</dt>
            <dd>
              {formatTokens(totals.cacheReadInputTokens)} / {formatTokens(totals.cacheCreationInputTokens)}
            </dd>
          </div>
        </dl>
      </section>

      {isEmptyUsage(usage) ? (
        <EmptyState
          icon={<IconBarChart width={20} height={20} />}
          title="No AI calls recorded yet"
          description="Nothing has been sent to the AI provider since the API started. When no provider key is configured, the development stub answers instead and is not counted."
        />
      ) : (
        <>
          <section className="panel" aria-labelledby="ai-usage-models">
            <h2 id="ai-usage-models" className="panel-title">
              By model
            </h2>
            <div className="table-scroll">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col">Provider</th>
                    <th scope="col">Model</th>
                    <th scope="col">Calls</th>
                    <th scope="col">Input tokens</th>
                    <th scope="col">Output tokens</th>
                  </tr>
                </thead>
                <tbody>
                  {modelsByTokens(usage).map((m) => (
                    <tr key={`${m.provider}/${m.model}`}>
                      <td>{m.provider}</td>
                      <td className="code">{m.model}</td>
                      <td>{formatTokens(m.totals.requests)}</td>
                      <td>{formatTokens(m.totals.inputTokens)}</td>
                      <td>{formatTokens(m.totals.outputTokens)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>

          <section className="panel" aria-labelledby="ai-usage-recent">
            <h2 id="ai-usage-recent" className="panel-title">
              Latest calls
            </h2>
            <p className="panel-sub">The {usage.recent.length} most recent (at most 50), newest first.</p>
            <div className="table-scroll">
              <table className="data-table">
                <thead>
                  <tr>
                    <th scope="col">When</th>
                    <th scope="col">Model</th>
                    <th scope="col">Input tokens</th>
                    <th scope="col">Output tokens</th>
                  </tr>
                </thead>
                <tbody>
                  {usage.recent.map((c, i) => (
                    <tr key={`${c.at}-${i}`}>
                      <td>{formatCallTime(c.at)}</td>
                      <td className="code">{c.model}</td>
                      <td>{formatTokens(c.usage.inputTokens)}</td>
                      <td>{formatTokens(c.usage.outputTokens)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        </>
      )}
    </>
  );
}
