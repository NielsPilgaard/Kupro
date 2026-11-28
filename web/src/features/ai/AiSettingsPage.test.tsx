import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { client } from '../../api/client';
import type {
  AiCapability,
  AiConnectionResponse,
  AiRouteResponse,
  AiStatusResponse,
  MeetingSettingsResponse,
  MemberResponse,
  WorkspaceResponse,
} from '../../api/gen/types.gen';
import { UiProvider } from '../../app/UiContext';
import { ToastProvider } from '../../components/Toasts';
import i18n from '../../i18n';
import { AiSettingsPage } from './AiSettingsPage';

vi.mock('@tanstack/react-router', async (original) => ({
  ...(await original<typeof import('@tanstack/react-router')>()),
  useParams: () => ({ workspace: 'acme' }),
  Link: ({ children }: { children: React.ReactNode }) => <a href="/">{children}</a>,
}));

const workspace = (role: WorkspaceResponse['role'], canManageBilling: boolean): WorkspaceResponse => ({
  id: 'w1',
  slug: 'acme',
  name: 'Acme',
  hoursPerWeek: 37,
  role,
  seats: 6,
  guests: 0,
  guestLimit: 30,
  canManageBilling,
  timeRegistration: false,
});

const settings: MeetingSettingsResponse = {
  defaultMode: 'suggest',
  fullAutoEnabled: false,
  fullAutoOperations: [],
  transcriptRetentionDays: 0,
  stats: [],
  statsByModel: [],
  proposalModel: null,
  proposalModelByLanguage: {},
  usage: { hoursUsed: 3.5, hoursIncluded: 12, resetsOn: '2026-12-01', spent: false },
};

const kuproRoute = (capability: AiCapability): AiRouteResponse => ({
  capability,
  language: null,
  connectionId: null,
  connectionName: 'Kupro',
  model: capability === 'transcription' ? 'whisper-large-v3' : 'deepseek-v4.1-flash',
  testedModel: true,
  paidBy: 'kupro',
  available: true,
});

const connection = (over: Partial<AiConnectionResponse> = {}): AiConnectionResponse => ({
  id: 'c1',
  providerType: 'scaleway',
  name: 'Scaleway',
  endpoint: null,
  projectId: null,
  keyHint: 'a3f9',
  status: 'healthy',
  lastError: null,
  lastCheckedAt: '2026-11-26T09:14:00Z',
  euConfirmedById: null,
  euConfirmedAt: null,
  createdAt: '2026-11-20T10:00:00Z',
  ...over,
});

const people: MemberResponse[] = [
  { id: 'm1', kind: 'human', name: 'Ada Owner', email: 'ada@acme.dk', role: 'owner', canManageBilling: true, status: 'active', ownerName: null, joinedAt: null },
  { id: 'm2', kind: 'human', name: 'Bo Billing', email: 'bo@acme.dk', role: 'member', canManageBilling: true, status: 'active', ownerName: null, joinedAt: null },
  { id: 'm3', kind: 'human', name: 'Cy Member', email: 'cy@acme.dk', role: 'member', canManageBilling: false, status: 'active', ownerName: null, joinedAt: null },
];

/** A fake API: the AI status, and connections a vendor accepts unless the key contains "bad". */
function fakeApi(info: WorkspaceResponse, status: AiStatusResponse) {
  const writes: { method: string; path: string; body: string }[] = [];
  client.setConfig({
    baseUrl: 'http://test',
    fetch: async (input: RequestInfo | URL) => {
      const request = input as Request;
      const url = new URL(request.url);
      const path = url.pathname.replace('/api/v1/workspaces/acme', '');
      if (request.method !== 'GET') {
        const body = await request.clone().text();
        writes.push({ method: request.method, path: path + url.search, body });
        if (request.method === 'POST' && path === '/ai/connections') {
          const sent = JSON.parse(body) as { key: string };
          const bad = sent.key.includes('bad');
          const added = connection({
            id: `c${writes.length}`,
            keyHint: sent.key.slice(-4),
            status: bad ? 'rejected' : 'healthy',
            lastError: bad ? '401 Unauthorized: the key is invalid or was revoked.' : null,
          });
          status.connections.push(added);
          return Response.json(added, { status: 201 });
        }
        if (request.method === 'PUT' && path === '/ai/routes') {
          const route = JSON.parse(body) as { capability: AiCapability; connectionId: string };
          const routed: AiRouteResponse = { ...kuproRoute(route.capability), connectionId: route.connectionId, connectionName: 'Scaleway', paidBy: 'workspace' };
          status.routes = status.routes.map((r) => (r.capability === route.capability ? routed : r));
          return Response.json(routed);
        }
        if (request.method === 'DELETE' && path.startsWith('/ai/connections/')) {
          const id = path.split('/').pop();
          status.connections = status.connections.filter((c) => c.id !== id);
          status.routes = status.routes.map((r) => (r.connectionId === id ? kuproRoute(r.capability) : r));
          return new Response(null, { status: 204 });
        }
        return Response.json({}, { status: 404 });
      }

      switch (path) {
        case '':
          return Response.json(info);
        case '/ai':
          return Response.json(status);
        case '/ai/providers':
          return Response.json([
            { id: 'scaleway', name: 'Scaleway', customEndpoint: false, capabilities: ['transcription', 'meetingProposals', 'voiceCommands'], transcriptionModel: 'whisper-large-v3', chatModel: 'deepseek-v4.1-flash' },
            { id: 'openai-compatible', name: 'OpenAI-compatible', customEndpoint: true, capabilities: ['transcription', 'meetingProposals', 'voiceCommands'], transcriptionModel: null, chatModel: null },
          ]);
        case '/meeting-settings':
          return Response.json(settings);
        case '/members':
          return Response.json(people);
        default:
          return Response.json([]);
      }
    },
  });
  return writes;
}

const notConnected = (): AiStatusResponse => ({
  connections: [],
  routes: (['transcription', 'meetingProposals', 'voiceCommands'] as const).map(kuproRoute),
  usage: [{ paidBy: 'kupro', audioMinutes: 180, inputTokens: 1000, outputTokens: 200, estimatedEuro: 0.6 }],
  period: '2026-11-01',
});

function open() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <ToastProvider>
        <UiProvider>
          <AiSettingsPage />
        </UiProvider>
      </ToastProvider>
    </QueryClientProvider>,
  );
}

window.matchMedia = () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }) as unknown as MediaQueryList;

describe('AiSettingsPage', () => {
  afterEach(cleanup);
  beforeEach(async () => {
    await i18n.changeLanguage('en');
  });

  it('leads with the included hours and connects a Scaleway key in three steps', async () => {
    const status = notConnected();
    const writes = fakeApi(workspace('owner', true), status);
    open();

    const usage = await screen.findByTestId('ai-usage');
    expect(within(usage).getByText('3.5h')).toBeInTheDocument();
    expect(within(usage).getByText('of 12h')).toBeInTheDocument();
    // The owner's pick note: no price for Scaleway anywhere on the page.
    expect(document.body.textContent).not.toMatch(/€|EUR|euro/i);

    fireEvent.click(screen.getByRole('button', { name: 'Go unlimited' }));
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getByText('Step 1 of 3')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Use another EU endpoint instead' })).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: 'I have a key' }));

    expect(within(dialog).getByText('Step 2 of 3')).toBeInTheDocument();
    const key = within(dialog).getByLabelText(/^Secret key/);
    expect(key).toHaveFocus();
    fireEvent.change(key, { target: { value: 'scw-secret-a3f9' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Check key' }));

    expect(await within(dialog).findByText('Scaleway accepted the key, and the models Kupro needs are available.')).toBeInTheDocument();
    // Checking adds the connection but routes nothing: that waits for step 3.
    expect(JSON.parse(writes[0]?.body ?? '')).toEqual({ providerType: 'scaleway', key: 'scw-secret-a3f9', projectId: null });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Continue' }));

    expect(within(dialog).getByText('Step 3 of 3')).toBeInTheDocument();
    expect(within(dialog).getByText(/no hour limit, and Scaleway bills the workspace directly/)).toBeInTheDocument();
    expect(within(dialog).getByText(/Kupro won't switch back to its own key/)).toBeInTheDocument();
    const boxes = within(dialog).getAllByRole('checkbox');
    expect(boxes).toHaveLength(3);
    for (const box of boxes) expect(box).toBeChecked();
    fireEvent.click(within(dialog).getByRole('checkbox', { name: /Voice commands/ }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    const routes = writes.filter((w) => w.path === '/ai/routes').map((w) => JSON.parse(w.body) as { capability: string });
    expect(routes.map((r) => r.capability)).toEqual(['transcription', 'meetingProposals']);
    expect(writes.some((w) => w.method === 'DELETE')).toBe(false);

    const card = await screen.findByTestId('ai-connection');
    expect(within(card).getByText('••••a3f9')).toBeInTheDocument();
    expect(within(card).getByText('Working')).toBeInTheDocument();
    expect(within(card).getByText('Transcription')).toBeInTheDocument();
    expect(within(card).queryByText('Voice commands')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Go unlimited' })).not.toBeInTheDocument();
  });

  it("shows the vendor's refusal, and cancelling removes the key it added", async () => {
    const status = notConnected();
    const writes = fakeApi(workspace('member', true), status);
    open();

    fireEvent.click(await screen.findByRole('button', { name: 'Go unlimited' }));
    const dialog = screen.getByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'I have a key' }));
    fireEvent.change(within(dialog).getByLabelText(/^Secret key/), { target: { value: 'bad-key-1234' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Check key' }));

    expect(await within(dialog).findByText('Scaleway refused the key: 401 Unauthorized: the key is invalid or was revoked.')).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Continue' })).not.toBeInTheDocument();

    fireEvent.keyDown(dialog, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await waitFor(() => expect(writes.map((w) => `${w.method} ${w.path}`)).toEqual(['POST /ai/connections', 'DELETE /ai/connections/c1']));
    expect(writes.some((w) => w.path === '/ai/routes')).toBe(false);
  });

  it('explains a rejected key: what is off, no fallback to Kupro, and what to do', async () => {
    const status = notConnected();
    const rejected = connection({ status: 'rejected', lastError: '403 Forbidden: permission denied.' });
    status.connections = [rejected];
    status.routes = status.routes.map((r) => ({ ...r, connectionId: 'c1', connectionName: 'Scaleway', paidBy: 'workspace', available: false }));
    status.routes = status.routes.map((r) => (r.capability === 'meetingProposals' ? { ...r, model: 'mistral-small-3.2', testedModel: false } : r));
    const writes = fakeApi(workspace('owner', true), status);
    open();

    const banner = await screen.findByTestId('ai-rejected');
    expect(banner).toHaveAttribute('role', 'alert');
    expect(within(banner).getByText("Scaleway rejected the workspace's key")).toBeInTheDocument();
    expect(within(banner).getByText(/403 Forbidden: permission denied\./)).toBeInTheDocument();
    expect(within(banner).getByText(/The microphone is off, and typed text uses keyword matching\. Kupro doesn't switch to its own key\./)).toBeInTheDocument();

    const card = screen.getByTestId('ai-connection');
    expect(within(card).getByText('Rejected')).toBeInTheDocument();
    expect(within(card).getByText('Not tested by Kupro')).toBeInTheDocument();

    // Disconnect asks in the page first, then goes back to the included hours.
    fireEvent.click(within(card).getByRole('button', { name: 'Disconnect' }));
    const confirm = within(card).getByRole('alertdialog');
    expect(writes).toHaveLength(0);
    fireEvent.click(within(confirm).getByRole('button', { name: 'Disconnect' }));
    await waitFor(() => expect(screen.queryByTestId('ai-connection')).not.toBeInTheDocument());
    expect(writes.map((w) => `${w.method} ${w.path}`)).toEqual(['DELETE /ai/connections/c1']);
    expect(await screen.findByRole('button', { name: 'Go unlimited' })).toBeInTheDocument();
  });

  it('shows a member without billing access the usage read-only, with who to ask', async () => {
    const status = notConnected();
    status.connections = [connection()];
    status.routes = status.routes.map((r) => (r.capability === 'transcription' ? { ...r, connectionId: 'c1', connectionName: 'Scaleway', paidBy: 'workspace' } : r));
    status.usage.push({ paidBy: 'workspace', audioMinutes: 41, inputTokens: 12000, outputTokens: 400, estimatedEuro: 0.2 });
    fakeApi(workspace('member', false), status);
    open();

    const own = await screen.findByTestId('ai-usage-own');
    expect(within(own).getByText('41 min')).toBeInTheDocument();
    expect(within(own).getByText(/12,400 tokens/)).toBeInTheDocument();
    expect(await screen.findByText(/Ask Ada Owner or Bo Billing\./)).toBeInTheDocument();
    expect(screen.getByTestId('ai-read-only')).toHaveTextContent('Only Owners and members with billing access can change this');

    const card = screen.getByTestId('ai-connection');
    expect(within(card).getByText('Working')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Go unlimited' })).not.toBeInTheDocument();
    expect(within(card).queryByRole('button')).not.toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/€|EUR|euro/i);
  });
});
