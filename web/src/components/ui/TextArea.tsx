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
  return (
    <div className={`field${error ? ' has-error' : ''}`}>
      <label className="field-label" htmlFor={inputId}>
        {label}
      </label>
      <textarea
        id={inputId}
        className="req-textarea"
        value={value}
        rows={rows}
        onChange={(e) => onChange(e.target.value)}
        aria-invalid={Boolean(error)}
        {...rest}
      />
      {error ? <span className="field-error-text">{error}</span> : hint ? <span className="field-hint">{hint}</span> : null}
    </div>
  );
}
