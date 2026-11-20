// Project opening and current-stage visibility: the stage pill tones, the project card (current stage
// first, Open link, Reviewer gate), the lifecycle stepper's states and key, the work-area badge, the
// read-only project team card, and the sidebar's Projects entry. The API, auth and project context are
// mocked; nothing here reaches a server.
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest';
import type { Project, ProjectMember } from '../../src/api/types';

const auth = vi.hoisted(() => ({ roles: [] as string[], userId: 'u-me' }));
vi.mock('../../src/auth/AuthContext', () => ({
  useAuth: () => ({ token: 'test-token', userId: auth.userId, roles: auth.roles, hasRole: (r: string) => auth.roles.includes(r) }),
}));

const ctx = vi.hoisted(() => ({
  project: null as Project | null,
  last: null as { id: string; name: string } | null,
  lifecycle: null as unknown,
  reloadLifecycle: () => undefined,
}));
vi.mock('../../src/context/ProjectContext', () => {
  const value = () => ({ projectId: ctx.project?.id ?? null, project: ctx.project, lifecycle: ctx.lifecycle, lifecycleFailed: false, reloadLifecycle: ctx.reloadLifecycle });
  return { useProject: value, useProjectContext: value, useOptionalProjectContext: value, readLastProject: () => ctx.last };
});
vi.mock('../../src/context/SidebarContext', () => ({
  useSidebar: () => ({ collapsed: false, toggleCollapsed: () => undefined, mobileOpen: false, closeMobile: () => undefined }),
}));

const api = vi.hoisted(() => ({ getMembers: vi.fn() }));
vi.mock('../../src/api/projects', () => ({ projectsApi: api }));
// The actions menu (edit/delete) is unchanged and tested by use; here it only has to render.
vi.mock('../../src/components/ProjectActionsMenu', () => ({ ProjectActionsMenu: () => <span data-testid="actions-menu" /> }));

import { statusVariant } from '../../src/lib/format';
import { ProjectCard } from '../../src/components/ProjectCard';
import { LifecycleLegend, LifecycleStepper } from '../../src/components/LifecycleStepper';
import { PhasePage } from '../../src/components/PhasePage';
import { ProjectTeamCard } from '../../src/components/ProjectTeamCard';
import { Sidebar } from '../../src/components/Sidebar';

const project = (status: number, over: Partial<Project> = {}): Project => ({
  id: 'p1',
  name: 'Triage Portal',
  description: 'Emergency triage.',
  status,
  createdAt: '2026-10-01T00:00:00Z',
  updatedAt: '2026-10-20T00:00:00Z',
  ...over,
});

const member = (over: Partial<ProjectMember>): ProjectMember => ({
  id: over.userId ?? 'm',
  projectId: 'p1',
  userId: 'u',
  email: 'x@example.com',
  displayName: 'Someone',
  addedByUserId: 'u-admin',
  addedAt: '2026-10-01T00:00:00Z',
  role: 'Developer',
  isActive: true,
  ...over,
});

const inRouter = (ui: React.ReactNode) => render(<MemoryRouter>{ui}</MemoryRouter>);

beforeEach(() => {
  auth.roles = [];
  ctx.project = null;
  ctx.last = null;
  ctx.lifecycle = null;
  ctx.reloadLifecycle = () => undefined;
  api.getMembers.mockResolvedValue([]);
  window.matchMedia ??= ((query: string) => ({
    matches: false,
    media: query,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  })) as unknown as typeof window.matchMedia;
});
afterEach(() => {
  cleanup();
  localStorage.clear();
});

describe('stage pill tones', () => {
  test('in-flight stages are blue, Approved and Completed have their own tones, none is a warning', () => {
    expect(statusVariant(0)).toBe('neutral');
    for (const s of [1, 2, 3, 5, 6]) expect(statusVariant(s)).toBe('info');
    expect(statusVariant(4)).toBe('approved');
    expect(statusVariant(7)).toBe('success');
    expect([0, 1, 2, 3, 4, 5, 6, 7].map(statusVariant)).not.toContain('warning');
  });
});

describe('project card', () => {
  test('leads with the current stage, names the next one and links to the project', () => {
    inRouter(<ProjectCard project={project(2)} onUpdated={() => undefined} />);
    expect(screen.getByText('Current stage')).toBeTruthy();
    expect(screen.getByText('Analysis').className).toContain('tone-info');
    expect(screen.getByText(/Next:/).textContent).toContain('Design');
    expect(screen.queryByText('Needs Reviewer approval')).toBeNull();
    const open = screen.getByRole('link', { name: /Open Project/ });
    expect(open.getAttribute('href')).toBe('/projects/p1');
    expect(screen.getByRole('link', { name: 'Triage Portal' }).getAttribute('href')).toBe('/projects/p1');
  });

  test('says when leaving the stage needs a Reviewer, and shows a pending request', () => {
    const pending = { fromStatus: 3, toStatus: 4 } as never;
    inRouter(<ProjectCard project={project(3)} lifecycle={{ pendingTransition: pending, evidenceGaps: [] } as never} onUpdated={() => undefined} />);
    expect(screen.getByText('Needs Reviewer approval')).toBeTruthy();
    expect(screen.getByText('Awaiting review')).toBeTruthy();
  });

  test('a completed project shows the lifecycle as completed', () => {
    inRouter(<ProjectCard project={project(7)} onUpdated={() => undefined} />);
    expect(screen.getByText('Completed').className).toContain('tone-success');
    expect(screen.getByText('Lifecycle completed')).toBeTruthy();
  });
});

describe('lifecycle stepper', () => {
  test('marks passed, current and upcoming stages from the project status, in the existing order', () => {
    render(<LifecycleStepper status={2} />);
    const steps = screen.getAllByRole('listitem');
    expect(steps.map((li) => li.querySelector('.sdlc-label')?.textContent)).toEqual([
      'Draft',
      'Requirements Gathering',
      'Analysis',
      'Design',
      'Approved',
      'Implementation',
      'Testing',
      'Completed',
    ]);
    expect(steps.filter((li) => li.getAttribute('aria-current') === 'step').map((li) => li.textContent)).toEqual([expect.stringContaining('Analysis')]);
    expect(steps[0].className).toContain('completed');
    expect(steps[1].className).toContain('completed');
    expect(steps[2].className).toContain('current');
    expect(steps[3].className).toContain('upcoming');
    // The Reviewer gates (Design, Testing) are still announced and labelled.
    expect(within(steps[3]).getByLabelText(/Leaving it needs Reviewer approval/)).toBeTruthy();
    expect(within(steps[6]).getByText('Reviewer approval to leave')).toBeTruthy();
    expect(within(steps[2]).queryByText('Reviewer approval to leave')).toBeNull();
  });

  test('a pending request keeps the project in its stage and marks it awaiting review', () => {
    render(<LifecycleStepper status={3} pending />);
    const current = screen.getAllByRole('listitem').find((li) => li.getAttribute('aria-current') === 'step')!;
    expect(current.textContent).toContain('Design');
    expect(current.textContent).toContain('Awaiting review');
  });

  test('the compact stepper has no gate text', () => {
    render(<LifecycleStepper status={3} compact />);
    expect(screen.queryByText('Reviewer approval to leave')).toBeNull();
  });

  test('the key explains every marker', () => {
    render(<LifecycleLegend />);
    expect(screen.getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      'Completed',
      'Current stage',
      'Upcoming',
      'Reviewer approval needed to leave this stage',
    ]);
  });
});

describe('work-area badge', () => {
  const renderArea = (status: number, phaseKey: string) => {
    ctx.project = project(status);
    return inRouter(
      <PhasePage phaseKey={phaseKey} title="Area" description="What happens here.">
        <p>content</p>
      </PhasePage>,
    );
  };

  test('names the project stage and how the page relates to it', () => {
    renderArea(3, 'analysis');
    expect(screen.getByText(/Project is in/).textContent).toBe('Project is in Design · This page supports Analysis, already passed');
  });

  test('current and later stages', () => {
    renderArea(2, 'analysis');
    expect(screen.getByText(/Project is in/).textContent).toBe('Project is in Analysis · This page is for the current stage');
    cleanup();
    renderArea(2, 'testing');
    expect(screen.getByText(/Project is in/).textContent).toBe('Project is in Analysis · This page supports Testing, a later stage');
  });
});

describe('project team card', () => {
  test('lists members with their role, in role order, read-only', async () => {
    api.getMembers.mockResolvedValue([
      member({ userId: 'u-qa', displayName: 'Quinn', role: 'QA' }),
      member({ userId: 'u-me', displayName: 'Bea', role: 'BusinessAnalyst' }),
      member({ userId: 'u-rev', displayName: 'Rae', role: 'Reviewer', isActive: false }),
    ]);
    inRouter(<ProjectTeamCard projectId="p1" />);
    await waitFor(() => expect(screen.getByText('Quinn')).toBeTruthy());
    const rows = screen.getAllByRole('listitem').map((li) => li.textContent);
    // Each row: avatar initials, name, role.
    expect(rows).toEqual(['BEBea (you)Business Analyst', 'QUQuinnQA', 'RARaeReviewer / Manager · deactivated']);
    expect(api.getMembers).toHaveBeenCalledWith('p1', 'test-token');
    expect(screen.queryByRole('button')).toBeNull();
    expect(screen.getByRole('link', { name: 'View members' }).getAttribute('href')).toBe('/projects/p1/gathering?tab=members');
  });

  test('says when the team cannot be loaded or is empty', async () => {
    api.getMembers.mockRejectedValueOnce(new Error('down'));
    inRouter(<ProjectTeamCard projectId="p1" />);
    await waitFor(() => expect(screen.getByText(/unavailable right now/)).toBeTruthy());
    cleanup();
    api.getMembers.mockResolvedValueOnce([]);
    inRouter(<ProjectTeamCard projectId="p1" />);
    await waitFor(() => expect(screen.getByText('No members have been added yet.')).toBeTruthy());
  });
});

describe('sidebar', () => {
  test('Projects stays reachable when the project sections are folded away', () => {
    localStorage.setItem('aireap.nav-sections-closed', JSON.stringify({ lifecycle: true, tools: true }));
    inRouter(<Sidebar />);
    expect(screen.getByRole('link', { name: 'Projects' }).getAttribute('href')).toBe('/projects');
    // The folded sections still fold (their toggles report closed).
    expect(screen.getByRole('button', { name: /^Project tools/ }).getAttribute('aria-expanded')).toBe('false');
    expect(screen.getByRole('button', { name: /^Lifecycle stages/ }).getAttribute('aria-expanded')).toBe('false');
  });

  test('lists the nine stages in order and marks the stage the work is in', () => {
    ctx.project = project(2);
    inRouter(<Sidebar />);
    const stages = within(screen.getByRole('region', { name: 'Lifecycle stages' }));
    const links = stages.getAllByRole('link');
    expect(links.map((a) => a.querySelector('.nav-label')?.textContent)).toEqual([
      'Gathering',
      'Analysis',
      'Validation',
      'Design',
      'Development',
      'Testing',
      'Traceability',
      'AI Copilot',
      'Review',
    ]);
    expect(links[0].textContent).toContain('Completed');
    expect(links[1].textContent).toContain('Now');
    expect(links[2].textContent).toContain('Now');
    expect(links[3].textContent).toContain('Not started');
    expect(links[1].getAttribute('href')).toBe('/projects/p1/analysis');
  });

  test('outside a project, says which project the work-area links open', () => {
    ctx.last = { id: 'p9', name: 'Billing Revamp' };
    inRouter(<Sidebar />);
    expect(screen.getByText(/Project links open your last project/)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Billing Revamp' }).getAttribute('href')).toBe('/projects/p9');
    expect(screen.getByRole('link', { name: 'Analysis' }).getAttribute('href')).toBe('/projects/p9/analysis');
  });

  test('admin-only modules stay hidden from other roles', () => {
    auth.roles = ['Developer'];
    inRouter(<Sidebar />);
    expect(screen.queryByRole('link', { name: 'Users' })).toBeNull();
    cleanup();
    auth.roles = ['Administrator'];
    inRouter(<Sidebar />);
    expect(screen.getByRole('link', { name: 'Users' })).toBeTruthy();
  });
});

// ---- Stage gaps: a "Needs attention" stage lists what is missing, and how to fix it ---------------

const guidance = (area: string, title: string) => ({
  key: 'k-' + title,
  kind: 'approval',
  howTo: `How to fix ${title}.`,
  dependsOn: [],
  blockers: [
    {
      reason: 'NeedsApproval',
      kind: 'approval',
      title,
      instruction: `Approve every ${title}.`,
      fixRoles: ['Reviewer'],
      callerCanFix: true,
      assignees: [],
      destination: { area, section: 'sec-api' },
      items: [{ artifactId: 'a1', code: 'API-001', title: 'Orders API', artifactType: 7, status: 1 }],
      totalCount: 1,
      hiddenCount: 0,
    },
  ],
});
const lifecycleState = (status: number, over: Record<string, unknown> = {}) => ({
  projectId: 'p1',
  status,
  stageLabel: 'Completed',
  ownerRole: 'Reviewer',
  nextIsGated: false,
  requiresSignOff: false,
  exitChecks: [],
  readyToAdvance: false,
  pendingTransition: null,
  evidenceGaps: [],
  actions: { canAdvance: false, canSubmitForApproval: false, canDecide: false, canWithdraw: false, canRollback: false, canOverride: false },
  history: [],
  ...over,
});
const designGap = () =>
  lifecycleState(7, {
    evidenceGaps: [
      {
        stage: 3,
        label: 'Design',
        ownerRole: 'Developer',
        passed: false,
        checks: [
          { label: 'Solution design approved', passed: true, detail: 'All approved.' },
          { label: 'API design approved', passed: false, detail: '1 API specification is not approved.', guidance: guidance('design', 'API specifications awaiting approval') },
        ],
      },
    ],
  });

describe('stage gaps panel', () => {
  test('a Needs attention stage names each gap, why, who and where to fix it', async () => {
    ctx.project = project(7);
    ctx.lifecycle = designGap();
    inRouter(<PhasePage phaseKey="design"><p>content</p></PhasePage>);
    const panel = screen.getByRole('region', { name: /Needs attention/ });
    expect(within(panel).getByText('API design approved')).toBeTruthy();
    expect(within(panel).getByText('1 API specification is not approved.')).toBeTruthy();
    // Only the failing check is listed; a passing one is not shown as a gap.
    expect(within(panel).queryByText('Solution design approved')).toBeNull();
    // Guidance is open on the first gap: the blocking artifact and the place it is fixed.
    expect(within(panel).getByRole('link', { name: /API-001/ }).getAttribute('href')).toBe('/projects/p1/design?focus=a1');
    expect(within(panel).getByRole('link', { name: /Go to Design/ }).getAttribute('href')).toBe('/projects/p1/design?section=sec-api');
    expect(within(panel).getByLabelText('1 outstanding')).toBeTruthy();
    expect(within(panel).getByRole('link', { name: /Ask Copilot/ }).getAttribute('href')).toContain('/projects/p1/copilot?phase=design&q=');
    // The header pill agrees.
    expect(screen.getAllByText('Needs attention').length).toBeGreaterThan(0);
  });

  test('a gap that is fixed moves to Resolved, with a success message, and the stage is Completed again', async () => {
    ctx.project = project(7);
    ctx.lifecycle = designGap();
    const view = inRouter(<PhasePage phaseKey="design"><p>content</p></PhasePage>);
    // The shared lifecycle state is re-read after the approval: the gap is gone.
    ctx.lifecycle = lifecycleState(7);
    view.rerender(<MemoryRouter><PhasePage phaseKey="design"><p>content</p></PhasePage></MemoryRouter>);
    const panel = screen.getByRole('region', { name: /Nothing outstanding/ });
    expect(within(panel).getByText('Resolved during this visit')).toBeTruthy();
    expect(within(panel).getByText('API design approved')).toBeTruthy();
    expect(within(panel).getByText(/All gaps in Design are resolved/)).toBeTruthy();
    expect(screen.queryByText('Needs attention')).toBeNull();
    expect(screen.getAllByText('Completed').length).toBeGreaterThan(0);
  });

  test('Re-check asks for a fresh lifecycle state', () => {
    const reload = vi.fn();
    ctx.reloadLifecycle = reload;
    ctx.project = project(7);
    ctx.lifecycle = designGap();
    inRouter(<PhasePage phaseKey="design"><p>content</p></PhasePage>);
    screen.getByRole('button', { name: /Re-check/ }).click();
    expect(reload).toHaveBeenCalledTimes(1);
  });

  test('the current stage lists its exit checks as to-dos, met and not met, never as Needs attention', () => {
    ctx.project = project(3);
    ctx.lifecycle = lifecycleState(3, {
      exitChecks: [
        { label: 'Solution design approved', passed: true, detail: 'All approved.' },
        { label: 'API design approved', passed: false, detail: '1 API specification is not approved.', guidance: guidance('design', 'API specifications awaiting approval') },
      ],
    });
    inRouter(<PhasePage phaseKey="design"><p>content</p></PhasePage>);
    const panel = screen.getByRole('region', { name: /What this stage still needs/ });
    expect(within(panel).getByText(/To finish this stage · 1 of 2 met/)).toBeTruthy();
    expect(within(panel).getByText('Solution design approved')).toBeTruthy();
    expect(screen.queryByText('Needs attention')).toBeNull();
  });

  test('the sidebar shows the same status and a gap counter', () => {
    ctx.project = project(7);
    ctx.lifecycle = designGap();
    inRouter(<Sidebar />);
    const design = screen.getByRole('link', { name: /Design/ });
    expect(design.textContent).toContain('1');
    expect(design.textContent).toContain('gap to resolve');
    expect(screen.getByText(/1 stage needs attention/)).toBeTruthy();
  });
});
