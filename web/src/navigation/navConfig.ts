import type { ComponentType, SVGProps } from 'react';
import { IconBarChart, IconCheckCircle, IconCode, IconEye, IconFolder, IconGrid, IconLayers, IconLink, IconSearch, IconSettings, IconShieldCheck, IconSparkles, IconUpload, IconUsers } from '../components/icons';
import { STAGES, type StageKey } from '../lib/stages';

/** Active = implemented, in-progress = partially implemented, planned = not implemented. */
export type NavStatus = 'active' | 'in-progress' | 'planned';

export const STATUS_LABEL: Record<NavStatus, string> = {
  active: 'Active',
  'in-progress': 'In Progress',
  planned: 'Planned',
};

export interface NavModule {
  key: string;
  label: string;
  icon: ComponentType<SVGProps<SVGSVGElement>>;
  status: NavStatus;
  description: string;
  /** Route segment under /projects/:id. Present for project-scoped modules. */
  segment?: string;
  /** Query string appended to a project-scoped route, e.g. `tab=documents`. */
  tab?: string;
  /** Absolute route, for workspace-level modules. */
  to?: string;
  role?: string;
  /** One of the nine workspace stages (lib/stages); drives the numbered, status-marked entry. */
  stageKey?: StageKey;
  /** Marks modules powered by the AI agents, for the section styling. */
  ai?: boolean;
}

export interface NavGroup {
  key: string;
  label: string;
  /** Shown under the group label to say what the group is for. */
  hint?: string;
  /** Sections that can be folded away by the user. */
  collapsible: boolean;
  /** Only meaningful with a project (the links go to the open or last-opened project). */
  projectScoped?: boolean;
  modules: NavModule[];
}

/** Tab a shared route shows when the URL has no `?tab=`. Modules on that route are told apart by tab. */
export const SEGMENT_DEFAULT_TAB: Record<string, string> = {
  gathering: 'context',
  copilot: 'ask',
};

const STAGE_ICONS: Record<StageKey, ComponentType<SVGProps<SVGSVGElement>>> = {
  gathering: IconUpload,
  analysis: IconSearch,
  validation: IconCheckCircle,
  design: IconLayers,
  development: IconCode,
  testing: IconShieldCheck,
  traceability: IconLink,
  copilot: IconSparkles,
  review: IconEye,
};

/*
 * Page → navigation map. Every module points at an existing route; nothing is duplicated, and no
 * route was removed. The sidebar is organised around the user's workflow:
 *
 *   Workspace          Dashboard, Projects            (application-wide)
 *   Lifecycle stages   the nine stages, 1 → 9          (project: the work, in order)
 *   Project tools      Stakeholders & team, Agent pipeline (project: supporting tools)
 *   Administration     Users (Roles & Audit Logs from there), Settings (Administrators only)
 *
 * Scope & objectives are the Gathering stage's default "Project context" tab.
 */
export const NAV_GROUPS: NavGroup[] = [
  {
    key: 'main',
    label: 'Workspace',
    collapsible: false,
    // Projects lives here, in a group that cannot be folded away, so the way into a project is always visible.
    modules: [
      { key: 'dashboard', label: 'Dashboard', icon: IconGrid, status: 'active', description: 'Your projects at a glance and what needs attention.', to: '/dashboard' },
      { key: 'projects', label: 'Projects', icon: IconFolder, status: 'active', description: 'Create, edit, update, delete and open projects.', to: '/projects' },
    ],
  },
  {
    key: 'lifecycle',
    label: 'Lifecycle stages',
    hint: 'The nine stages of a project, in order',
    collapsible: true,
    projectScoped: true,
    modules: STAGES.map((s) => ({
      key: s.key,
      label: s.name,
      icon: STAGE_ICONS[s.key],
      status: 'active' as const,
      description: s.summary,
      segment: s.segment,
      stageKey: s.key,
      ai: s.key === 'copilot' || undefined,
    })),
  },
  {
    key: 'tools',
    label: 'Project tools',
    collapsible: true,
    projectScoped: true,
    modules: [
      { key: 'stakeholders', label: 'Stakeholders', icon: IconUsers, status: 'active', segment: 'gathering', tab: 'stakeholders', description: 'Who is affected by, and who decides on, the project.' },
      { key: 'pipeline', label: 'Agent pipeline', icon: IconBarChart, status: 'active', segment: 'copilot', tab: 'pipeline', ai: true, description: 'Run and review the AI agents that generate project artifacts.' },
    ],
  },
  {
    key: 'management',
    label: 'Administration',
    collapsible: true,
    modules: [
      { key: 'users', label: 'Users', icon: IconUsers, status: 'active', description: 'Create accounts and activate or deactivate them.', to: '/admin/users', role: 'Administrator' },
      { key: 'settings', label: 'Settings', icon: IconSettings, status: 'active', description: 'AI configuration and system settings.', to: '/settings', role: 'Administrator' },
      { key: 'ai-usage', label: 'AI Usage', icon: IconBarChart, status: 'active', description: 'Tokens used by AI calls since the API started.', to: '/admin/ai-usage', role: 'Administrator' },
    ],
  },
];

export const ALL_MODULES: NavModule[] = NAV_GROUPS.flatMap((g) => g.modules);

export function findModule(key: string | null): NavModule | null {
  return key ? (ALL_MODULES.find((m) => m.key === key) ?? null) : null;
}
