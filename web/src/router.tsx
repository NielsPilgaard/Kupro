import type { QueryClient } from '@tanstack/react-query';
import { createRootRouteWithContext, createRoute, createRouter } from '@tanstack/react-router';
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

const indexRoute = createRoute({ getParentRoute: () => rootRoute, path: '/', component: HomePage });

const routeTree = rootRoute.addChildren([indexRoute]);

export function createAppRouter(queryClient: QueryClient) {
  return createRouter({ routeTree, context: { queryClient }, defaultPreload: 'intent' });
}

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
