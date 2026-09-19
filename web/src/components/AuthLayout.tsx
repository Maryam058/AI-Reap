import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { IconCheckCircle, IconShieldCheck, IconSparkles } from './icons';

const FEATURES = [
  {
    icon: IconSparkles,
    tone: 'mint',
    title: 'AI-Assisted Requirements',
    description: 'Turn raw, ambiguous input into clear, structured, testable specs.',
  },
  {
    icon: IconShieldCheck,
    tone: 'blue',
    title: 'Full Traceability',
    description: 'Every artifact is versioned, linked, and audit-ready.',
  },
  {
    icon: IconCheckCircle,
    tone: 'teal',
    title: 'Human-Approved Delivery',
    description: 'AI proposes the work — your team reviews and approves it.',
  },
];

export function AuthLayout({
  title,
  subtitle,
  children,
  footnote,
}: {
  title: string;
  subtitle?: string;
  children: ReactNode;
  footnote?: ReactNode;
}) {
  return (
    <div className="auth-screen">
      <div className="auth-hero-panel">
        <Link to="/login" className="auth-brand">
          <span className="auth-brand-mark">
            <IconSparkles />
          </span>
          <span>
            <span className="auth-brand-name">AI-REAP</span>
            <span className="auth-brand-tag">SDLC Automation</span>
          </span>
        </Link>

        <div className="auth-hero-copy">
          <h1 className="auth-hero-heading">
            Welcome back to your
            <br />
            <span className="auth-hero-accent">AI-powered workspace</span>
          </h1>
          <p className="auth-hero-sub">
            Structure requirements, trace every artifact, and ship with an AI assistant that proposes — while your
            team stays in control of every approval.
          </p>
        </div>

        <ul className="auth-feature-list">
          {FEATURES.map(({ icon: Icon, tone, title: featureTitle, description }) => (
            <li key={featureTitle}>
              <span className={`auth-feature-icon tone-${tone}`}>
                <Icon />
              </span>
              <span>
                <span className="auth-feature-title">{featureTitle}</span>
                <span className="auth-feature-desc">{description}</span>
              </span>
            </li>
          ))}
        </ul>
      </div>

      <div className="auth-main">
        <div className="auth-card">
          <h2>{title}</h2>
          {subtitle && <p className="auth-card-subtitle">{subtitle}</p>}
          {children}
          {footnote && <p className="auth-footnote">{footnote}</p>}
        </div>
      </div>
    </div>
  );
}
