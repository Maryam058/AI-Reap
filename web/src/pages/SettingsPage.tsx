import { PageHeader } from '../components/ui/PageHeader';
import { EmptyState } from '../components/ui/EmptyState';
import { IconSettings } from '../components/icons';
import { findModule, STATUS_LABEL } from '../navigation/navConfig';

// Settings is not built yet. This page only describes the planned module; it has no controls or data.
export function SettingsPage() {
  const settings = findModule('settings');
  return (
    <>
      <PageHeader
        title="Settings"
        subtitle="Platform-wide preferences, integrations and AI providers."
        actions={<span className="nav-badge nav-badge-planned">{STATUS_LABEL.planned}</span>}
      />
      <EmptyState
        icon={<IconSettings width={22} height={22} />}
        title="Settings are planned"
        description={settings?.description}
      />
    </>
  );
}
