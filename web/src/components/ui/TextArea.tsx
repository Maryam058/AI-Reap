import type { TextareaHTMLAttributes } from 'react';

interface TextAreaProps extends Omit<TextareaHTMLAttributes<HTMLTextAreaElement>, 'onChange'> {
  label: string;
  value: string;
  onChange: (value: string) => void;
  error?: string;
  hint?: string;
}

export function TextArea({ label, value, onChange, error, hint, id, rows = 3, ...rest }: TextAreaProps) {
  const inputId = id ?? label.toLowerCase().replace(/\s+/g, '-');
  // Callers mark optional fields with a trailing " (optional)" (as in TextField), drawn muted.
  const optional = label.endsWith(' (optional)');
  const noteId = error || hint ? `${inputId}-note` : undefined;
  return (
    <div className={`field${error ? ' has-error' : ''}`}>
      <label className="field-label" htmlFor={inputId}>
        {optional ? (
          <>
            {label.slice(0, -' (optional)'.length)} <span className="field-optional">optional</span>
          </>
        ) : (
          label
        )}
      </label>
      <textarea
        id={inputId}
        className="req-textarea"
        value={value}
        rows={rows}
        onChange={(e) => onChange(e.target.value)}
        aria-invalid={Boolean(error)}
        aria-describedby={noteId}
        {...rest}
      />
      {error ? (
        <span className="field-error-text" id={noteId} role="alert">
          {error}
        </span>
      ) : hint ? (
        <span className="field-hint" id={noteId}>
          {hint}
        </span>
      ) : null}
    </div>
  );
}
