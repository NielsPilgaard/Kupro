import '@fontsource-variable/bricolage-grotesque';
import '@fontsource-variable/hanken-grotesk';
import '@fontsource/fragment-mono';
import './styles/index.css';
import './i18n';
import './api/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from '@tanstack/react-router';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { UiProvider } from './app/UiContext';
import { ToastProvider } from './components/Toasts';
import { createAppRouter } from './router';

const queryClient = new QueryClient({
  defaultOptions: { queries: { staleTime: 30_000, refetchOnWindowFocus: false } },
});
const router = createAppRouter(queryClient);

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <UiProvider>
          <RouterProvider router={router} />
        </UiProvider>
      </ToastProvider>
    </QueryClientProvider>
  </StrictMode>,
);
