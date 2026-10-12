import type { QueryClient } from '@tanstack/react-query';
import { createRootRouteWithContext, createRoute, createRouter, Outlet, redirect } from '@tanstack/react-router';
import { AppShell } from './app/AppShell';
import { callbackPath, completeSignIn, getSession, signIn } from './auth/auth';
import { MyIssuesPage, ProjectIssuesPage, type View } from './features/issues/IssuePages';
import { SettingsPage } from './features/settings/SettingsPage';
import { PlanPage, type Zoom } from './features/plan/PlanPage';
import { SprintsPage } from './features/sprints/SprintsPage';
import { HomePage, NewProjectPage, WorkspaceIndexPage } from './features/workspace/WorkspacePages';
import { AuthCallbackError } from './routes/AuthCallbackError';
import { NotFound } from './routes/NotFound';
import { RootLayout } from './routes/RootLayout';

export interface RouterContext {
  queryClient: QueryClient;
}

/**
 * `?issue=FLOW-12` opens the detail panel over whatever view is showing, so every issue view is linkable;
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
const planRoute = createRoute({
  getParentRoute: () => workspaceRoute,
  path: '/plan',
  component: PlanPage,
  // Horizon, zoom and projects live in the URL (docs/delivery-plan.md: no plan entity).
  validateSearch: (search: Record<string, unknown>): { from?: string; to?: string; zoom?: Zoom; project?: string; issue?: string; meeting?: string } => ({
    ...(typeof search.from === 'string' ? { from: search.from } : {}),
    ...(typeof search.to === 'string' ? { to: search.to } : {}),
    ...(search.zoom === 'month' || search.zoom === 'quarter' || search.zoom === 'year' ? { zoom: search.zoom } : {}),
    ...(typeof search.project === 'string' ? { project: search.project } : {}),
    ...withIssue(search),
  }),
});
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
    workspaceRoute.addChildren([workspaceIndexRoute, newProjectRoute, myIssuesRoute, sprintsRoute, planRoute, projectRoute]),
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
