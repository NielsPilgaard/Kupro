import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { client } from '../api/client';
import i18n from '../i18n';
import { ApiStatus } from './ApiStatus';

function renderWithQuery() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <ApiStatus />
    </QueryClientProvider>,
  );
}

describe('ApiStatus', () => {
  afterEach(() => vi.restoreAllMocks());

  it('shows the API version from the generated client', async () => {
    await i18n.changeLanguage('en');
    client.setConfig({
      baseUrl: 'http://test',
      fetch: async () => Response.json({ service: 'flowstate', version: '1.2.3' }),
    });

    renderWithQuery();

    expect(await screen.findByText('API online · v1.2.3')).toBeInTheDocument();
  });

  it('shows an error when the API is unreachable', async () => {
    await i18n.changeLanguage('en');
    client.setConfig({ baseUrl: 'http://test', fetch: async () => new Response(null, { status: 503 }) });

    renderWithQuery();

    expect(await screen.findByText(/API unreachable/)).toBeInTheDocument();
  });
});
