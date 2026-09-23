export function initials(name: string | null | undefined): string {
  if (!name) return '?';
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}

export function firstName(name: string | null | undefined): string {
  if (!name) return 'there';
  return name.trim().split(/\s+/)[0] ?? name;
}

const STATUS_LABELS: Record<number, string> = {
  0: 'Draft',
  1: 'Requirements Gathering',
  2: 'Analysis',
  3: 'Design',
  4: 'Approved',
  5: 'Implementation',
  6: 'Testing',
  7: 'Completed',
};

export const SDLC_STAGES = [
  'Draft',
  'Requirements Gathering',
  'Analysis',
  'Design',
  'Approved',
  'Implementation',
  'Testing',
  'Completed',
];

export function statusLabel(status: number): string {
  return STATUS_LABELS[status] ?? `Status ${status}`;
}

export function statusVariant(status: number): string {
  if (status <= 0) return 'neutral';
  if (status <= 3) return 'info';
  if (status === 4) return 'success';
  if (status <= 6) return 'warning';
  return 'success';
}

export function stageProgress(status: number): number {
  const clamped = Math.min(Math.max(status, 0), SDLC_STAGES.length - 1);
  return Math.round((clamped / (SDLC_STAGES.length - 1)) * 100);
}

export function formatRelativeTime(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  const diffMs = Date.now() - date.getTime();
  const diffSec = Math.round(diffMs / 1000);
  const diffMin = Math.round(diffSec / 60);
  const diffHour = Math.round(diffMin / 60);
  const diffDay = Math.round(diffHour / 24);

  if (diffSec < 45) return 'just now';
  if (diffMin < 60) return `${diffMin}m ago`;
  if (diffHour < 24) return `${diffHour}h ago`;
  if (diffDay < 7) return `${diffDay}d ago`;
  return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
}

export function formatFullDateTime(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
}

export function greeting(now = new Date()): string {
  const h = now.getHours();
  return h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening';
}

export function formatFullDate(date: Date): string {
  return date.toLocaleDateString(undefined, { weekday: 'long', month: 'long', day: 'numeric' });
}
