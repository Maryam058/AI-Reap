import type { CopilotContextReport } from '../api/types';

// The server's default for Ai:Copilot:MaxQuestionChars, used until /copilot/limits has answered.
export const DEFAULT_MAX_QUESTION_CHARS = 2000;

/** A usable limit from the server, or the default when it is missing or not a positive whole number. */
export function effectiveMaxQuestionChars(value: unknown): number {
  return typeof value === 'number' && Number.isInteger(value) && value > 0 ? value : DEFAULT_MAX_QUESTION_CHARS;
}

/**
 * Why a question cannot be sent, or null when it can. Counts `value.length` (UTF-16 code units), as the
 * server does. A long question is never shortened - the user has to shorten it - so the input does not use
 * the `maxLength` attribute, which would silently cut pasted text.
 */
export function questionLengthError(question: string, max: number): string | null {
  if (question.length <= max) return null;
  return `Your question is ${question.length.toLocaleString('en-US')} characters long; the Copilot accepts at most ${max.toLocaleString('en-US')}. Shorten it to ask.`;
}

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/**
 * The notice shown under an answer whose project context was intentionally shortened, or null when it was
 * not (or the server sent no report). Says what was shortened, never that the full context was used.
 */
export function copilotContextNotice(report: CopilotContextReport | null | undefined): string | null {
  if (!report || !report.reduced) return null;

  const why =
    report.reason === 'providerBudget'
      ? `to fit the request limit of ${report.provider ?? 'the AI provider'}`
      : "by the Copilot's standard context limits";

  const artifacts: string[] = [];
  if (report.artifactsFull > 0) artifacts.push(`${report.artifactsFull} in full`);
  if (report.artifactsTruncated > 0) artifacts.push(`${report.artifactsTruncated} shortened`);
  if (report.artifactsTitleOnly > 0) artifacts.push(`${report.artifactsTitleOnly} by title only`);
  if (report.artifactsOmitted > 0) artifacts.push(`${report.artifactsOmitted} left out`);

  const other: string[] = [];
  if (report.versionsOmitted > 0) other.push(`${plural(report.versionsOmitted, 'change', 'changes')} from the version history`);
  if (report.impactCodesOmitted > 0) other.push(`${plural(report.impactCodesOmitted, 'impacted artifact', 'impacted artifacts')}`);
  if (report.excerptsOmitted != null && report.excerptsOmitted > 0) {
    other.push(`${plural(report.excerptsOmitted, 'document excerpt', 'document excerpts')}`);
  }

  const parts = [`This answer used reduced project context (${why}).`];
  if (artifacts.length > 0) parts.push(`Artifacts: ${artifacts.join(', ')}.`);
  if (other.length > 0) parts.push(`Also left out: ${other.join(', ')}.`);
  parts.push('Items left out still exist - the answer may be incomplete.');
  return parts.join(' ');
}
