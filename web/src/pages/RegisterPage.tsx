import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { ApiError } from '../api/client';
import { AuthLayout } from '../components/AuthLayout';
import { TextField } from '../components/ui/TextField';
import { PasswordField } from '../components/ui/PasswordField';
import { IconAlert, IconInfo, IconMail, IconUser } from '../components/icons';

const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

interface FieldErrors {
  displayName?: string;
  email?: string;
  password?: string;
  confirmPassword?: string;
}

export function RegisterPage() {
  const { register } = useAuth();
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({});
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const validate = (): boolean => {
    const errors: FieldErrors = {};
    if (!displayName.trim()) errors.displayName = 'Full name is required.';
    if (!email.trim()) errors.email = 'Work email is required.';
    else if (!EMAIL_PATTERN.test(email)) errors.email = 'Enter a valid email address.';
    if (!password) errors.password = 'Password is required.';
    else if (password.length < 8) errors.password = 'Use at least 8 characters.';
    if (!confirmPassword) errors.confirmPassword = 'Confirm your password.';
    else if (confirmPassword !== password) errors.confirmPassword = 'Passwords do not match.';
    setFieldErrors(errors);
    return Object.keys(errors).length === 0;
  };

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    if (!validate()) return;
    setSubmitting(true);
    try {
      await register({ email, password, displayName });
      navigate('/projects');
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Registration failed. Please try again.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <AuthLayout
      title="Create your account"
      subtitle="Start building better software requirements today."
      footnote={
        <>
          Already have an account? <Link to="/login">Sign in</Link>
        </>
      }
    >
      <form onSubmit={onSubmit} noValidate>
        {error && (
          <div className="alert alert-danger">
            <IconAlert />
            <span>{error}</span>
          </div>
        )}

        <TextField
          label="Full name"
          value={displayName}
          onChange={(v) => {
            setDisplayName(v);
            if (fieldErrors.displayName) setFieldErrors((f) => ({ ...f, displayName: undefined }));
          }}
          icon={<IconUser />}
          error={fieldErrors.displayName}
          placeholder="Jane Doe"
          autoComplete="name"
        />
        <TextField
          label="Work email"
          type="email"
          value={email}
          onChange={(v) => {
            setEmail(v);
            if (fieldErrors.email) setFieldErrors((f) => ({ ...f, email: undefined }));
          }}
          icon={<IconMail />}
          error={fieldErrors.email}
          placeholder="you@company.com"
          autoComplete="email"
        />
        <PasswordField
          label="Password"
          value={password}
          onChange={(v) => {
            setPassword(v);
            if (fieldErrors.password) setFieldErrors((f) => ({ ...f, password: undefined }));
          }}
          error={fieldErrors.password}
          hint={fieldErrors.password ? undefined : 'At least 8 characters.'}
          autoComplete="new-password"
        />
        <PasswordField
          label="Confirm password"
          value={confirmPassword}
          onChange={(v) => {
            setConfirmPassword(v);
            if (fieldErrors.confirmPassword) setFieldErrors((f) => ({ ...f, confirmPassword: undefined }));
          }}
          error={fieldErrors.confirmPassword}
          autoComplete="new-password"
        />

        <div className="auth-phase-note">
          <IconInfo />
          <span>
            New accounts have no access until an administrator assigns you a role (Business Analyst, Developer, QA,
            Reviewer / Manager). Ask your administrator once you have signed up.
          </span>
        </div>

        <button type="submit" className="btn btn-primary btn-block" disabled={submitting}>
          {submitting ? 'Creating account…' : 'Create account'}
        </button>
      </form>
    </AuthLayout>
  );
}
