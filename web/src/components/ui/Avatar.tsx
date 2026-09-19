import { initials } from '../../lib/format';

export function Avatar({ name, size = 'md' }: { name: string | null | undefined; size?: 'md' | 'lg' }) {
  return <div className={`avatar${size === 'lg' ? ' avatar-lg' : ''}`}>{initials(name)}</div>;
}
