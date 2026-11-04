import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { client } from '../../api/client';
import { listIssuesQueryKey } from '../../api/gen/@tanstack/react-query.gen';
import type { IssueDetail, IssueSummary } from '../../api/gen/types.gen';
import { UiProvider } from '../../app/UiContext';
import { ToastProvider } from '../../components/Toasts';
import i18n from '../../i18n';
import { IssuePanel } from './IssuePanel';

vi.mock('@tanstack/react-router', async (original) => ({
  ...(await original<typeof import('@tanstack/react-router')>()),
  useNavigate: () => vi.fn(),
  Link: ({ children }: { children: React.ReactNode }) => <a href="/">{children}</a>,
}));

const row: IssueSummary = {
  id: '00000000-0000-0000-0000-000000000001',
  key: 'CORE-1',
  type: 'task',
  title: 'Render curves',
  state: 'inProgress',
  priority: 'high',
  assignee: { id: 'u1', name: 'Ada Lovelace', kind: 'human' },
  parentKey: null,
  effort: 3,
  startDate: null,
  dueDate: null,
  labels: ['ui'],
  rank: 1,
  sprintId: null,
  createdAt: '2026-11-01T10:00:00Z',
  updatedAt: '2026-11-01T10:00:00Z',
};

const detail: IssueDetail = {
  ...row,
  projectKey: 'CORE',
  description: 'The real description',
  parent: null,
  childEffort: null,
  children: [],
  relations: [],
  createdBy: { id: 'u1', name: 'Ada Lovelace', kind: 'human' },
  completedAt: null,
};

window.matchMedia = () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }) as unknown as MediaQueryList;

describe('IssuePanel', () => {
  let release: (issue: IssueDetail | null) => void;
  let writes: string[];

  afterEach(cleanup);

  beforeEach(async () => {
    await i18n.changeLanguage('en');
    writes = [];
    const detailResponse = new Promise<IssueDetail | null>((resolve) => (release = resolve));

    client.setConfig({
      baseUrl: 'http://test',
      fetch: async (input: RequestInfo | URL) => {
        const request = input as Request;
        if (request.method !== 'GET') {
          writes.push(await request.clone().text());
          return Response.json(detail);
        }

        if (!new URL(request.url).pathname.endsWith('/issues/CORE-1')) return Response.json([]);
        const issue = await detailResponse;
        return issue ? Response.json(issue) : Response.json({ code: 'issue_not_found' }, { status: 404 });
      },
    });
  });

  function open(cached: IssueSummary[] = [row], workspace = 'acme') {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    if (cached.length) qc.setQueryData(listIssuesQueryKey({ path: { workspace }, query: { limit: 200 } }), { items: cached, nextCursor: null });
    render(
      <QueryClientProvider client={qc}>
        <ToastProvider>
          <UiProvider>
            <IssuePanel workspace="acme" issueKey="CORE-1" />
          </UiProvider>
        </ToastProvider>
      </QueryClientProvider>,
    );
  }

  const title = () => screen.queryByLabelText<HTMLTextAreaElement>('Title');
  const descriptionTab = () => screen.getByRole('tabpanel');

  it('shows the list row at once while the detail is delayed', async () => {
    open();

    expect(title()).toHaveValue('Render curves');
    expect(screen.getByRole('button', { name: /^State:/ })).toHaveTextContent('In Progress');
    expect(screen.getByRole('button', { name: /^Priority:/ })).toHaveTextContent('High');
    expect(screen.getByRole('button', { name: /^Assignee:/ })).toHaveTextContent('Ada Lovelace');
    expect(screen.getByRole('button', { name: /^Labels:/ })).toHaveTextContent('ui');
    expect(screen.getByLabelText(i18n.t('issue.effort'))).toHaveValue('3');

    release(detail);
    expect(await screen.findByText('The real description')).toBeInTheDocument();
  });

  it('never shows an empty placeholder as real data', async () => {
    open();

    expect(descriptionTab()).toHaveTextContent('Loading…');
    expect(screen.queryByText(i18n.t('issue.descriptionPlaceholder'))).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('tab', { name: 'Subtasks' }));
    expect(descriptionTab()).toHaveTextContent('Loading…');
    expect(screen.queryByText(i18n.t('issue.noSubtasks'))).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('tab', { name: 'Relations' }));
    expect(descriptionTab()).toHaveTextContent('Loading…');
    expect(screen.queryByRole('button', { name: i18n.t('issue.addRelation') })).not.toBeInTheDocument();

    release(detail);
    expect(await screen.findByRole('button', { name: i18n.t('issue.addRelation') })).toBeInTheDocument();
  });

  it('cannot save over a description it has not loaded', async () => {
    open();

    fireEvent.keyDown(document.body, { key: 'e' });
    expect(screen.queryByRole('textbox', { name: 'Description' })).not.toBeInTheDocument();

    release(detail);
    const editor = await screen.findByRole('textbox', { name: 'Description' });
    expect(editor).toHaveValue('The real description');

    fireEvent.blur(editor);
    expect(writes).toEqual([]);
  });

  it('keeps every field read-only until the detail arrives', async () => {
    open();

    for (const name of [/^State:/, /^Priority:/, /^Assignee:/, /^Labels:/, /^Type:/, /^Sprint:/]) expect(screen.getByRole('button', { name })).toBeDisabled();
    expect(title()).toHaveAttribute('readonly');
    expect(screen.getByLabelText(i18n.t('issue.effort'))).toHaveAttribute('readonly');

    release(detail);
    await waitFor(() => expect(screen.getByRole('button', { name: /^State:/ })).toBeEnabled());
    expect(title()).not.toHaveAttribute('readonly');
    expect(screen.getByLabelText(i18n.t('issue.effort'))).not.toHaveAttribute('readonly');
  });

  it('replaces values from a stale row, so a blur cannot save them back', async () => {
    open([{ ...row, title: 'Old title', effort: 1 }]);
    expect(title()).toHaveValue('Old title');

    release(detail);
    await waitFor(() => expect(title()).toHaveValue('Render curves'));
    expect(screen.getByLabelText(i18n.t('issue.effort'))).toHaveValue('3');

    fireEvent.blur(title()!);
    fireEvent.blur(screen.getByLabelText(i18n.t('issue.effort')));
    expect(writes).toEqual([]);
  });

  it('takes the newest copy when lists disagree', () => {
    open([
      { ...row, title: 'Older' },
      { ...row, title: 'Newer', updatedAt: '2026-11-02T10:00:00Z' },
    ]);

    expect(title()).toHaveValue('Newer');
  });

  it('shows nothing until the response for a key in no cached list', async () => {
    open([]);
    expect(title()).not.toBeInTheDocument();

    release(detail);
    await waitFor(() => expect(title()).toHaveValue('Render curves'));
  });

  it('does not borrow a row from another workspace', () => {
    open([row], 'other');

    expect(title()).not.toBeInTheDocument();
  });

  it('shows the error when the issue turns out to be gone', async () => {
    open();
    expect(title()).toHaveValue('Render curves');

    release(null);
    expect(await screen.findByText(i18n.t('errors.issueNotFound', { key: 'CORE-1' }))).toBeInTheDocument();
    expect(title()).not.toBeInTheDocument();
  });
});
