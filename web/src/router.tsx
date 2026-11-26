import type { QueryClient } from '@tanstack/react-query';
import { createRootRouteWithContext, createRoute, createRouter, Outlet, redirect } from '@tanstack/react-router';
import { AppShell } from './app/AppShell';
import { callbackPath, completeSignIn, getSession, signIn } from './auth/auth';
import { ImportPage, ImportsPage } from './features/imports/ImportsPage';
import { ImportWizardPage, type WizardSearch } from './features/imports/ImportWizard';
import type { StepId } from './features/imports/wizard';
import { MyIssuesPage, ProjectIssuesPage, type View } from './features/issues/IssuePages';
import { MembersPage } from './features/members/MembersPage';
import { SettingsPage } from './features/settings/SettingsPage';
import { WorkspaceSettingsPage } from './features/settings/WorkspaceSettingsPage';
import { MyTimePage, type TimeSearch } from './features/time/MyTimePage';
import { TeamTimePage } from './features/time/TeamTimePage';
import { PlanPage, type Zoom } from './features/plan/PlanPage';
import { SprintReportPage } from './features/sprints/SprintReportPage';
import { SprintsPage } from './features/sprints/SprintsPage';
import { HomePage, NewProjectPage, WorkspaceIndexPage } from './features/workspace/WorkspacePages';
import { AuthCallbackError } from './routes/AuthCallbackError';
import { NotFound } from './routes/NotFound';
import { RootLayout } from './routes/RootLayout';

export interface RouterContext {
  queryClient: QueryClient;
}

/**
 * `?issue=KUP-12` opens the detail panel over whatever view is showing, so every issue view is linkable;
 * `?meeting=<id>` opens the meeting agent's panel (task 015) the same way.
 */
const withIssue = (search: Record<string, unknown>): { issue?: string; meeting?: string } => ({
  ...(typeof search.issue === 'string' ? { issue: search.issue } : {}),
  ...(typeof search.meeting === 'string' ? { meeting: search.meeting } : {}),
});

const rootRoute = createRootRouteWithContext<RouterContext>()({
  component: RootLayout,
  notFoundComponent: NotFound,
});

const callbackRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: callbackPath,
  beforeLoad: async () => {
    const returnTo = await completeSignIn();
    throw redirect({ href: returnTo, replace: true });
  },
  errorComponent: AuthCallbackError,
});

/** Everything under this layout requires a Keycloak session. */
const authedRoute = createRoute({
  getParentRoute: () => rootRoute,
  id: 'authed',
  component: Outlet,
  beforeLoad: async ({ location }) => {
    const session = (await getSession()) ?? (await signIn(location.href));
    return { session };
  },
});

const homeRoute = createRoute({ getParentRoute: () => authedRoute, path: '/', component: HomePage });
const settingsRoute = createRoute({ getParentRoute: () => authedRoute, path: '/settings', component: SettingsPage });

const workspaceRoute = createRoute({ getParentRoute: () => authedRoute, path: '/w/$workspace', component: AppShell });
const workspaceIndexRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/', component: WorkspaceIndexPage });
const newProjectRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/new-project', component: NewProjectPage });
const membersRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/members', component: MembersPage });
const importsRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/imports', component: ImportsPage });
const wizardSearch = (search: Record<string, unknown>): WizardSearch => ({
  ...(typeof search.step === 'string' ? { step: search.step as StepId } : {}),
  ...(typeof search.run === 'string' ? { run: search.run } : {}),
  ...(typeof search.total === 'number' ? { total: search.total } : {}),
  ...(search.first === true ? { first: true } : {}),
});
const newImportRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/imports/new', component: ImportWizardPage, validateSearch: wizardSearch });
const importSetupRoute = createRoute({
  getParentRoute: () => workspaceRoute,
  path: '/imports/$importId/setup',
  component: ImportWizardPage,
  validateSearch: wizardSearch,
});
const importRoute = createRoute({
  getParentRoute: () => workspaceRoute,
  path: '/imports/$importId',
  component: ImportPage,
  validateSearch: (search: Record<string, unknown>): { tab?: 'runs' | 'conflicts' | 'people'; issue?: string; meeting?: string } => ({
    ...(search.tab === 'runs' || search.tab === 'conflicts' || search.tab === 'people' ? { tab: search.tab } : {}),
    ...withIssue(search),
  }),
});
const myIssuesRoute = createRoute({
  getParentRoute: () => workspaceRoute,
  path: '/my-issues',
  component: MyIssuesPage,
  validateSearch: withIssue,
});
const sprintsRoute = createRoute({
  getParentRoute: () => workspaceRoute,
  path: '/sprints',
  component: SprintsPage,
  validateSearch: (search: Record<string, unknown>): { project?: string; issue?: string; meeting?: string } => ({
    ...(typeof search.project === 'string' ? { project: search.project } : {}),
    ...withIssue(search),
  }),
});
const sprintReportRoute = createRoute({
  getParentRoute: () => workspaceRoute,
  path: '/sprints/$sprintId',
  component: SprintReportPage,
  validateSearch: withIssue,
});
const planRoute = createRoute({
  getParentRoute: () => workspaceRoute,
  path: '/plan',
  component: PlanPage,
  // Horizon, zoom and projects live in the URL (docs/delivery-plan.md: no plan entity).
  validateSearch: (search: Record<string, unknown>): { from?: string; to?: string; zoom?: Zoom; project?: string; epic?: string; issue?: string; meeting?: string } => ({
    ...(typeof search.from === 'string' ? { from: search.from } : {}),
    ...(typeof search.to === 'string' ? { to: search.to } : {}),
    ...(search.zoom === 'month' || search.zoom === 'quarter' || search.zoom === 'year' ? { zoom: search.zoom } : {}),
    ...(typeof search.project === 'string' ? { project: search.project } : {}),
    ...(typeof search.epic === 'string' ? { epic: search.epic } : {}),
    ...withIssue(search),
  }),
});
/** `?week=` is any day of the week shown; the pages clamp it to this week at the latest. */
const timeSearch = (search: Record<string, unknown>): TimeSearch => ({
  ...(typeof search.week === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(search.week) ? { week: search.week } : {}),
  ...withIssue(search),
});
const myTimeRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/time', component: MyTimePage, validateSearch: timeSearch });
const teamTimeRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/team-time', component: TeamTimePage, validateSearch: timeSearch });
const workspaceSettingsRoute = createRoute({ getParentRoute: () => workspaceRoute, path: '/settings', component: WorkspaceSettingsPage });
const projectRoute = createRoute({
  getParentRoute: () => workspaceRoute,
  path: '/p/$project',
  component: ProjectIssuesPage,
  validateSearch: (search: Record<string, unknown>) => ({
    view: (search.view === 'board' ? 'board' : 'list') as View,
    ...withIssue(search),
  }),
});

const routeTree = rootRoute.addChildren([
  callbackRoute,
  authedRoute.addChildren([
    homeRoute,
    settingsRoute,
    workspaceRoute.addChildren([
      workspaceIndexRoute,
      newProjectRoute,
      membersRoute,
      importsRoute,
      newImportRoute,
      importSetupRoute,
      importRoute,
      myIssuesRoute,
      sprintsRoute,
      sprintReportRoute,
      planRoute,
      myTimeRoute,
      teamTimeRoute,
      workspaceSettingsRoute,
      projectRoute,
    ]),
  ]),
]);

export function createAppRouter(queryClient: QueryClient) {
  return createRouter({ routeTree, context: { queryClient }, defaultPreload: 'intent' });
}

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
