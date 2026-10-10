import type { QueryClient } from '@tanstack/react-query';
import { createRootRouteWithContext, createRoute, createRouter, redirect } from '@tanstack/react-router';
import { callbackPath, completeSignIn, getSession, signIn } from './auth/auth';
import { AuthCallbackError } from './routes/AuthCallbackError';
import { AuthedLayout } from './routes/AuthedLayout';
import { HomePage } from './routes/HomePage';
import { NotFound } from './routes/NotFound';
import { RootLayout } from './routes/RootLayout';

export interface RouterContext {
  queryClient: QueryClient;
}

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
  component: AuthedLayout,
  beforeLoad: async ({ location }) => {
    const session = (await getSession()) ?? (await signIn(location.href));
    return { session };
  },
});

const indexRoute = createRoute({ getParentRoute: () => authedRoute, path: '/', component: HomePage });

const routeTree = rootRoute.addChildren([callbackRoute, authedRoute.addChildren([indexRoute])]);

export function createAppRouter(queryClient: QueryClient) {
  return createRouter({ routeTree, context: { queryClient }, defaultPreload: 'intent' });
}

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
