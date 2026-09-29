import type { ComponentType, SVGProps } from 'react';
import {
  IconBarChart,
  IconCheckCircle,
  IconCode,
  IconDocument,
  IconFolder,
  IconGrid,
  IconLayers,
  IconRefresh,
  IconRocket,
  IconSearch,
  IconSettings,
  IconShieldCheck,
  IconSparkles,
  IconTarget,
  IconUsers,
} from '../components/icons';

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
  /** Lifecycle stages (Project.status values) this module's work serves; drives its done/current marker. */
  stages?: number[];
  /** Marks modules powered by the AI agents, for the section styling. */
  ai?: boolean;
}

export interface NavGroup {
  key: string;
  label: string;
  /** Sections that can be folded away by the user. */
  collapsible: boolean;
  modules: NavModule[];
}

/** Tab a shared route shows when the URL has no `?tab=`. Modules on that route are told apart by tab. */
export const SEGMENT_DEFAULT_TAB: Record<string, string> = {
  gathering: 'context',
  copilot: 'ask',
};

/*
 * Page → navigation map. Every module points at an existing route; nothing is duplicated.
 * There is no separate project browser: /projects is the one place to create, edit, delete and open projects.
 *
 *   Dashboard             /dashboard
 *   Projects              /projects, /projects/new, /projects/:id, /projects/:id/edit
 *   Requirements          /projects/:id/gathering?tab=sources
 *   Stakeholders          /projects/:id/gathering?tab=stakeholders
 *   Scope & Objectives    /projects/:id/gathering (project context tab)
 *   Analysis              /projects/:id/analysis
 *   Design                /projects/:id/design
 *   Development           /projects/:id/development
 *   Testing               /projects/:id/testing
 *   Deployment, Maint.    not built yet (shown as Soon, no route)
 *   AI Assistant          /projects/:id/copilot
 *   Agent Pipeline        /projects/:id/copilot?tab=pipeline
 *   Quality & Validation  /projects/:id/validation
 *   Users                 /admin/users (Roles & Audit Logs are reached from that page)
 *   Settings              /settings
 *
 * Traceability is reachable from the Testing work area and the work-area navigation.
 */
export const NAV_GROUPS: NavGroup[] = [
  {
    key: 'main',
    label: 'Main',
    collapsible: false,
    modules: [{ key: 'dashboard', label: 'Dashboard', icon: IconGrid, status: 'active', description: 'Portfolio and lifecycle progress overview.', to: '/dashboard' }],
  },
  {
    key: 'project',
    label: 'Project',
    collapsible: true,
    modules: [
      { key: 'projects', label: 'Projects', icon: IconFolder, status: 'active', description: 'Create, edit, update, delete and open projects.', to: '/projects' },
      { key: 'requirements', label: 'Requirements', icon: IconDocument, status: 'active', segment: 'gathering', tab: 'sources', stages: [1], description: 'Raw requirement sources that feed the analysis.' },
      { key: 'stakeholders', label: 'Stakeholders', icon: IconUsers, status: 'active', segment: 'gathering', tab: 'stakeholders', description: 'Who is affected by, and who decides on, the project.' },
      { key: 'scope', label: 'Scope & Objectives', icon: IconTarget, status: 'active', segment: 'gathering', tab: 'context', description: 'Business context, goals and scope of the project.' },
    ],
  },
  {
    key: 'sdlc',
    label: 'SDLC',
    collapsible: true,
    modules: [
      { key: 'analysis', label: 'Analysis', icon: IconSearch, status: 'active', segment: 'analysis', stages: [2], description: 'Analyze raw input into structured functional and non-functional requirements.' },
      { key: 'design', label: 'Design', icon: IconLayers, status: 'in-progress', segment: 'design', stages: [3], description: 'Turn validated requirements into architecture, data and interfaces.' },
      { key: 'development', label: 'Development', icon: IconCode, status: 'in-progress', segment: 'development', stages: [4, 5], description: 'Break the design into implementation tasks and track their completion.' },
      { key: 'testing', label: 'Testing', icon: IconShieldCheck, status: 'in-progress', segment: 'testing', stages: [6], description: 'Derive test cases from requirements and track coverage.' },
      { key: 'deployment', label: 'Deployment', icon: IconRocket, status: 'planned', description: 'Release planning and environments.' },
      { key: 'maintenance', label: 'Maintenance', icon: IconRefresh, status: 'planned', description: 'Change requests and post-release support.' },
    ],
  },
  {
    key: 'ai',
    label: 'AI & Automation',
    collapsible: true,
    modules: [
      { key: 'copilot', label: 'AI Assistant', icon: IconSparkles, status: 'active', segment: 'copilot', tab: 'ask', ai: true, description: 'Ask about gaps, coverage and recent changes, grounded in project data.' },
      { key: 'pipeline', label: 'Agent Pipeline', icon: IconBarChart, status: 'active', segment: 'copilot', tab: 'pipeline', ai: true, description: 'Run and review the AI agents that generate project artifacts.' },
      { key: 'validation', label: 'Quality & Validation', icon: IconCheckCircle, status: 'in-progress', segment: 'validation', stages: [2], ai: true, description: 'Check requirements for ambiguity, conflicts and completeness.' },
    ],
  },
  {
    key: 'management',
    label: 'Management',
    collapsible: true,
    modules: [
      { key: 'users', label: 'Users', icon: IconUsers, status: 'active', description: 'Create accounts and activate or deactivate them.', to: '/admin/users', role: 'Administrator' },
      { key: 'settings', label: 'Settings', icon: IconSettings, status: 'active', description: 'AI configuration and system settings.', to: '/settings', role: 'Administrator' },
    ],
  },
];

export const ALL_MODULES: NavModule[] = NAV_GROUPS.flatMap((g) => g.modules);

export function findModule(key: string | null): NavModule | null {
  return key ? (ALL_MODULES.find((m) => m.key === key) ?? null) : null;
}
