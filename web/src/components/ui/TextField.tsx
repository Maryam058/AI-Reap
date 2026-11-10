import type { InputHTMLAttributes, ReactNode } from 'react';

interface TextFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'onChange'> {
  label: string;
  value: string;
  onChange: (value: string) => void;
  icon?: ReactNode;
  error?: string;
  hint?: string;
  suffix?: ReactNode;
}

export function TextField({ label, value, onChange, icon, error, hint, suffix, id, ...rest }: TextFieldProps) {
  const inputId = id ?? label.toLowerCase().replace(/\s+/g, '-');
  // Callers mark required fields with a trailing " *"; draw the star in the danger tone so it stands out.
  const required = label.endsWith(' *');
  // …and optional ones with a trailing " (optional)", drawn muted.
  const optional = label.endsWith(' (optional)');
  const noteId = error || hint ? `${inputId}-note` : undefined;

  return (
    <div className={`field${error ? ' has-error' : ''}`}>
      <label className="field-label" htmlFor={inputId}>
        {required ? (
          <>
            {label.slice(0, -2)} <span className="field-required">*</span>
          </>
        ) : optional ? (
          <>
            {label.slice(0, -' (optional)'.length)} <span className="field-optional">optional</span>
          </>
        ) : (
          label
        )}
      </label>
      <div className={`field-input-wrap${icon ? ' has-icon' : ''}${suffix ? ' has-suffix' : ''}`}>
        {icon && <span className="field-icon">{icon}</span>}
        <input
          id={inputId}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          aria-invalid={Boolean(error)}
          aria-describedby={noteId}
          {...rest}
        />
        {suffix}
      </div>
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
