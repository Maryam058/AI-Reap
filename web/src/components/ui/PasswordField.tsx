import { useState } from 'react';
import type { InputHTMLAttributes } from 'react';
import { IconEye, IconEyeOff, IconLock } from '../icons';
import { TextField } from './TextField';

interface PasswordFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'onChange' | 'type'> {
  label: string;
  value: string;
  onChange: (value: string) => void;
  error?: string;
  hint?: string;
}

export function PasswordField({ label, value, onChange, error, hint, ...rest }: PasswordFieldProps) {
  const [visible, setVisible] = useState(false);

  return (
    <TextField
      label={label}
      type={visible ? 'text' : 'password'}
      value={value}
      onChange={onChange}
      icon={<IconLock />}
      error={error}
      hint={hint}
      suffix={
        <button
          type="button"
          className="field-suffix-btn"
          onClick={() => setVisible((v) => !v)}
          aria-label={visible ? 'Hide password' : 'Show password'}
          tabIndex={-1}
        >
          {visible ? <IconEyeOff /> : <IconEye />}
        </button>
      }
      {...rest}
    />
  );
}
