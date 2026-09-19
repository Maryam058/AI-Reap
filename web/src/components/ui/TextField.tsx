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

  return (
    <div className={`field${error ? ' has-error' : ''}`}>
      <label className="field-label" htmlFor={inputId}>
        {label}
      </label>
      <div className={`field-input-wrap${icon ? ' has-icon' : ''}${suffix ? ' has-suffix' : ''}`}>
        {icon && <span className="field-icon">{icon}</span>}
        <input
          id={inputId}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          aria-invalid={Boolean(error)}
          {...rest}
        />
        {suffix}
      </div>
      {error ? <span className="field-error-text">{error}</span> : hint ? <span className="field-hint">{hint}</span> : null}
    </div>
  );
}
