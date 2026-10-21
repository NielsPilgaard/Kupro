import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { Command } from 'cmdk';
import { CircleCheck, FolderKanban, Inbox, List, Plus, Search, Tag, UserRound, Users } from 'lucide-react';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { getMeOptions, listIssuesOptions, listLabelsOptions, listProjectsOptions } from '../../api/gen/@tanstack/react-query.gen';
import type { IssuePriority, IssueState } from '../../api/gen/types.gen';
import { useIssues, useMembers, useUpdateIssue } from '../../api/issues';
import { useUi, type PalettePage } from '../../app/UiContext';
import { Avatar, Kbd, LabelPill, PriorityIcon, StatusRing } from '../../components/ui';
import { cliFor, priorities, states } from '../../lib/issues';

interface Entry {
  id: string;
  label: string;
  icon?: ReactNode;
  hint?: ReactNode;
  cli?: string;
  run(): void;
}

/** Shortest text worth a server search; the API accepts the same syntax as the CLI and MCP (task 009). */
const MIN_SEARCH = 2;

/**
 * Ctrl/Cmd+K. Every action is here, and the footer shows the equivalent CLI command (parity made visible, design
 * guide). Pages double as the S/A/P/L pickers for the active issue.
 */
export function CommandPalette({ workspace }: { workspace: string }) {
  const { t } = useTranslation();
  const ui = useUi();
  const navigate = useNavigate();
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState('');
  const page = ui.palette;
  const key = ui.activeKey;

  const { data: me } = useQuery(getMeOptions());
  const { data: projects } = useQuery({ ...listProjectsOptions({ path: { workspace } }), enabled: !!page });
  const { data: labels } = useQuery({ ...listLabelsOptions({ path: { workspace } }), enabled: page === 'label' });
  const { data: members } = useMembers(workspace);
  const { data: recent } = useIssues(workspace, { sort: 'updated' });

  // Debounced server search: free text plus filters such as `state:todo assignee:me`.
  const [search, setSearch] = useState('');
  useEffect(() => {
    const id = setTimeout(() => setSearch(page === 'root' ? query.trim() : ''), 120);
    return () => clearTimeout(id);
  }, [query, page]);
  const searching = search.length >= MIN_SEARCH;
  const { data: found } = useQuery({
    ...listIssuesOptions({ path: { workspace }, query: { q: search, limit: 20 } }),
    enabled: searching,
    placeholderData: keepPreviousData,
  });
  const update = useUpdateIssue(workspace, members?.find((m) => m.kind === 'human' && m.email === me?.email)?.id);

  const close = () => {
    setQuery('');
    ui.closePalette();
  };
  const go = (to: PalettePage) => {
    setQuery('');
    ui.openPalette(to);
  };
  const change = (body: Parameters<typeof update.mutate>[0]['body']) => {
    if (key) update.mutate({ path: { workspace, key }, body });
    close();
  };

  const entries = useMemo<Entry[]>(() => {
    switch (page) {
      case 'state':
        return states.map((s: IssueState) => ({
          id: `state:${s}`,
          label: t(`state.${s}`),
          icon: <StatusRing state={s} />,
          cli: cliFor('state', key ?? undefined, s),
          run: () => change({ state: s }),
        }));
      case 'priority':
        return priorities.map((p: IssuePriority) => ({
          id: `priority:${p}`,
          label: t(`priority.${p}`),
          icon: <PriorityIcon priority={p} />,
          cli: cliFor('priority', key ?? undefined, p),
          run: () => change({ priority: p }),
        }));
      case 'assign':
        return [
          { id: 'assign:none', label: t('issue.unassigned'), icon: <Avatar member={null} size={16} />, run: () => change({ clear: ['assignee'] }) },
          ...(members ?? [])
            .filter((m) => m.status === 'active')
            .map((m) => ({
              id: `assign:${m.id}`,
              label: m.name,
              icon: <Avatar member={{ id: m.id, name: m.name, kind: m.kind }} size={16} />,
              hint: m.kind === 'agent' ? t('palette.agentFor', { name: m.ownerName }) : undefined,
              cli: cliFor('assign', key ?? undefined, m.email === me?.email ? 'me' : m.id),
              run: () => change({ assignee: m.id }),
            })),
        ];
      case 'label': {
        const typed = query.trim().toLowerCase();
        const existing = (labels ?? []).map((l) => ({
          id: `label:${l.name}`,
          label: l.name,
          icon: <LabelPill name={l.name} />,
          cli: cliFor('label', key ?? undefined, l.name),
          run: () => change({ addLabels: [l.name] }),
        }));
        return typed && !labels?.some((l) => l.name === typed)
          ? [...existing, { id: `label:new:${typed}`, label: t('palette.newLabel', { name: typed }), icon: <Tag size={14} />, cli: cliFor('label', key ?? undefined, typed), run: () => change({ addLabels: [typed] }) }]
          : existing;
      }
      default:
        return [
          { id: 'create', label: t('issue.new'), icon: <Plus size={16} />, hint: <Kbd>C</Kbd>, cli: cliFor('create'), run: () => { close(); ui.setCreateOpen(true); } },
          ...(key
            ? [
                { id: 'go:state', label: t('palette.changeState', { key }), icon: <CircleCheck size={16} />, hint: <Kbd>S</Kbd>, run: () => go('state') },
                { id: 'go:assign', label: t('palette.assign', { key }), icon: <UserRound size={16} />, hint: <Kbd>A</Kbd>, run: () => go('assign') },
                { id: 'go:priority', label: t('palette.priority', { key }), icon: <PriorityIcon priority="high" />, hint: <Kbd>P</Kbd>, run: () => go('priority') },
                { id: 'go:label', label: t('palette.label', { key }), icon: <Tag size={16} />, hint: <Kbd>L</Kbd>, run: () => go('label') },
                { id: 'assign:me', label: t('palette.assignMe', { key }), icon: <UserRound size={16} />, cli: cliFor('assign', key), run: () => change({ assignee: 'me' }) },
                { id: 'close', label: t('palette.close', { key }), icon: <StatusRing state="done" />, cli: cliFor('close', key), run: () => change({ state: 'done' }) },
              ]
            : []),
          { id: 'nav:mine', label: t('nav.myIssues'), icon: <Inbox size={16} />, cli: 'kupro issue list --assignee me', run: () => { close(); void navigate({ to: '/w/$workspace/my-issues', params: { workspace } }); } },
          { id: 'nav:members', label: t('palette.members'), icon: <Users size={16} />, cli: 'kupro member list', run: () => { close(); void navigate({ to: '/w/$workspace/members', params: { workspace } }); } },
          ...(projects ?? []).flatMap((p) => [
            { id: `nav:${p.key}:list`, label: t('palette.openList', { name: p.name }), icon: <List size={16} />, cli: `kupro issue list --project ${p.key}`, run: () => { close(); void navigate({ to: '/w/$workspace/p/$project', params: { workspace, project: p.key }, search: { view: 'list' } }); } },
            { id: `nav:${p.key}:board`, label: t('palette.openBoard', { name: p.name }), icon: <FolderKanban size={16} />, run: () => { close(); void navigate({ to: '/w/$workspace/p/$project', params: { workspace, project: p.key }, search: { view: 'board' } }); } },
          ]),
          ...((searching ? found?.items : recent?.items) ?? []).map((i) => ({
            id: `issue:${i.key}`,
            label: `${i.key} ${i.title}`,
            icon: <StatusRing state={i.state} />,
            cli: searching ? `kupro issue search ${search}` : `kupro issue get ${i.key}`,
            run: () => {
              close();
              ui.setActiveKey(i.key);
              void navigate({ to: '.', search: (prev: Record<string, unknown>) => ({ ...prev, issue: i.key }) });
            },
          })),
        ];
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [page, key, members, labels, projects, recent, found, searching, search, query, me, t]);

  if (!page) return null;
  // While searching, the server has already matched the issues; commands are matched here by a plain substring so
  // cmdk's fuzzy filter doesn't drop the typo-tolerant results.
  const needle = query.trim().toLowerCase();
  const shown = searching
    ? entries.filter((e) => e.id.startsWith('issue:') || e.label.toLowerCase().includes(needle))
    : entries;
  const current = shown.find((e) => e.id === selected) ?? shown[0];

  return (
    <div className="fixed inset-0 z-40 flex justify-center bg-scrim pt-11" onMouseDown={close}>
      <Command
        label={t('palette.title')}
        loop
        shouldFilter={!searching}
        value={searching ? (current?.id ?? '') : selected}
        onValueChange={setSelected}
        onMouseDown={(e) => e.stopPropagation()}
        onKeyDown={(e) => {
          if (e.key === 'Escape' || (e.key === 'Backspace' && !query && page !== 'root')) {
            e.preventDefault();
            e.stopPropagation();
            if (page === 'root' || e.key === 'Escape') close();
            else go('root');
          }
        }}
        className="flex h-fit max-h-[min(480px,80vh)] w-[min(540px,calc(100%-32px))] flex-col overflow-hidden rounded-r2 border border-line bg-panel shadow-float"
      >
        <div className="flex items-center gap-2 border-b border-line px-3">
          <Search size={16} strokeWidth={1.5} className="text-mute" aria-hidden="true" />
          {page !== 'root' && key && <span className="rounded-r1 bg-sunk px-1.5 font-mono text-sm text-mute">{key}</span>}
          <Command.Input
            autoFocus
            value={query}
            onValueChange={setQuery}
            placeholder={t(`palette.placeholder.${page}`)}
            className="h-11 min-w-0 flex-1 bg-transparent text-md outline-none focus-visible:outline-none"
          />
        </div>
        <Command.List className="min-h-0 overflow-auto p-1">
          <Command.Empty className="p-3 text-mute">{t('palette.empty')}</Command.Empty>
          {shown.map((entry) => (
            <Command.Item
              key={entry.id}
              value={entry.id}
              keywords={[entry.label]}
              onSelect={entry.run}
              className="flex h-9 cursor-pointer items-center gap-2 rounded-r1 px-2 text-md data-[selected=true]:bg-sel"
            >
              <span className="flex w-4 justify-center text-mute">{entry.icon}</span>
              <span className="min-w-0 flex-1 truncate">{entry.label}</span>
              {entry.hint && <span className="text-sm text-mute">{entry.hint}</span>}
            </Command.Item>
          ))}
        </Command.List>
        <footer className="truncate border-t border-line bg-sunk px-3 py-1.5 font-mono text-xs text-mute">
          {current?.cli ?? ' '}
        </footer>
      </Command>
    </div>
  );
}
