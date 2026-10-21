import type { AiPlanResult } from '../api/types';
import { attentionReasons, planNeedsAttention, planTone, providerLine, statusLabel } from '../lib/aiPlan';
import { IconAlert } from './icons';

/**
 * The dry-run AI plan under an Analyze / Generate button: how each configured provider would take
 * the selected source, labelled as an estimate made without contacting any AI provider. It only
 * informs - the button stays enabled, and the backend still refuses (saving nothing) a run a limit
 * applies to.
 */
export function AiPlanSummary({ plan, loading, failed }: { plan: AiPlanResult | null; loading: boolean; failed: boolean }) {
  if (loading && !plan) {
    return <p className="ai-plan ai-plan-muted">Checking AI request limits…</p>;
  }
  if (failed && !plan) {
    return <p className="ai-plan ai-plan-muted">AI plan unavailable - the run itself still checks every limit.</p>;
  }
  if (!plan) return null;

  const tone = planTone(plan.overall.status);
  const attention = planNeedsAttention(plan);
  const reasons = attentionReasons(plan);

  return (
    <div className={`ai-plan ai-plan-${tone}`} role={attention ? 'status' : undefined}>
      <div className="ai-plan-head">
        {attention && <IconAlert width={13} height={13} aria-hidden="true" />}
        <span className={`ai-plan-chip ai-plan-chip-${tone}`}>{statusLabel(plan.overall.status)}</span>
        <span className="ai-plan-label">AI plan · estimate, no AI provider contacted</span>
      </div>
      <ul className="ai-plan-providers">
        {plan.providers.map((p) => (
          <li key={p.role}>{providerLine(p)}</li>
        ))}
      </ul>
      {attention && <p className="ai-plan-summary">{plan.overall.summary}</p>}
      <details className="ai-plan-details">
        <summary>Details</summary>
        {reasons.length > 0 && (
          <ul>
            {reasons.map((r) => (
              <li key={`${r.provider}-${r.message}`}>
                <strong>{r.provider}:</strong> {r.message}
              </li>
            ))}
          </ul>
        )}
        <ul className="ai-plan-caveats">
          {plan.caveats.map((c) => (
            <li key={c}>{c}</li>
          ))}
        </ul>
      </details>
    </div>
  );
}
