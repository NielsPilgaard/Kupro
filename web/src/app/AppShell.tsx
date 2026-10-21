import { useQuery } from '@tanstack/react-query';
import { Link, Outlet, useNavigate, useParams, useSearch } from '@tanstack/react-router';
import { CalendarRange, CircleUser, IterationCw, Inbox, Layers, LogOut, PanelLeftClose, PanelLeftOpen, Plus, Search, Users } from 'lucide-react';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { getMeOptions, listProjectsOptions, listWorkspacesOptions } from '../api/gen/@tanstack/react-query.gen';
import { useRealtime } from '../api/realtime';
import { signOut } from '../auth/auth';
import { Kbd } from '../components/ui';
import { CommandPalette } from '../features/palette/CommandPalette';
import { CreateIssueDialog } from '../features/issues/CreateIssueDialog';
import { IssuePanel } from '../features/issues/IssuePanel';
import { MeetingPanel } from '../features/meetings/MeetingPanel';
import { useShortcuts } from '../lib/shortcuts';
import { projectColorClass } from '../lib/issues';
import { useUi } from './UiContext';

/** Sidebar 212 px + topbar 48 px + scrolling content; the shell itself never scrolls (design guide). */
export function AppShell() {
  const { i18n } = useTranslation();
  const ui = useUi();
  const { workspace } = useParams({ strict: false }) as { workspace: string };
  const search = useSearch({ strict: false }) as { issue?: string; meeting?: string };
  const navigate = useNavigate();
  const { data: me } = useQuery(getMeOptions());
  useRealtime(workspace, search.issue);

  // The profile's language wins over the browser's (ADR-011).
  useEffect(() => {
    if (me && me.locale !== i18n.language) void i18n.changeLanguage(me.locale);
  }, [me, i18n]);

  useShortcuts({
    'mod+k': () => ui.openPalette(),
    c: () => (ui.createOverride ? ui.createOverride() : ui.setCreateOpen(true)),
    '[': () => ui.toggleSidebar(),
    s: () => ui.activeKey && ui.openPalette('state'),
    a: () => ui.activeKey && ui.openPalette('assign'),
    p: () => ui.activeKey && ui.openPalette('priority'),
    l: () => ui.activeKey && ui.openPalette('label'),
    escape: () => {
      if (search.issue) void navigate({ to: '.', search: (prev: Record<string, unknown>) => ({ ...prev, issue: undefined }) });
    },
  });

  return (
    <div className="flex h-full">
      <Sidebar workspace={workspace} collapsed={ui.sidebarCollapsed} />
      <div className="flex min-w-0 flex-1 flex-col">
        <Outlet />
      </div>
      {search.issue && <IssuePanel workspace={workspace} issueKey={search.issue} meName={me?.displayName} />}
      {search.meeting && <MeetingPanel workspace={workspace} meetingId={search.meeting} />}
      <CommandPalette workspace={workspace} />
      <CreateIssueDialog workspace={workspace} />
    </div>
  );
}

function Sidebar({ workspace, collapsed }: { workspace: string; collapsed: boolean }) {
  const { t } = useTranslation();
  const ui = useUi();
  const { data: workspaces } = useQuery(listWorkspacesOptions());
  const { data: projects } = useQuery(listProjectsOptions({ path: { workspace } }));
  const current = workspaces?.find((w) => w.slug === workspace);
  const nav = 'flex h-[var(--nav-item)] items-center gap-2 rounded-r1 px-2 text-side-ink hover:bg-side-sel';
  const active = { className: 'bg-side-sel font-semibold' };

  return (
    <nav
      aria-label={t('nav.label')}
      className={`flex shrink-0 flex-col gap-4 bg-side p-2 text-side-ink transition-[width] duration-[var(--dur-1)] ${collapsed ? 'w-[52px]' : 'w-[var(--sidebar)]'} max-sm:hidden`}
    >
      <div className="flex h-9 items-center gap-2 px-1">
        <svg width="20" height="20" viewBox="0 0 32 32" aria-hidden="true" className="shrink-0">
          <rect x="3" y="6.5" width="17" height="5" rx="2.5" className="fill-side-ink" />
          <rect x="9" y="13.5" width="20" height="5" rx="2.5" className="fill-acc" />
          <rect x="5" y="20.5" width="12" height="5" rx="2.5" className="fill-side-ink" opacity={0.5} />
        </svg>
        {!collapsed && <span className="truncate font-semibold">{current?.name ?? workspace}</span>}
        <button
          type="button"
          onClick={ui.toggleSidebar}
          className="ml-auto rounded-r1 p-1 hover:bg-side-sel"
          aria-label={t(collapsed ? 'nav.expand' : 'nav.collapse')}
          title={`${t(collapsed ? 'nav.expand' : 'nav.collapse')} ([)`}
        >
          {collapsed ? <PanelLeftOpen size={16} strokeWidth={1.5} /> : <PanelLeftClose size={16} strokeWidth={1.5} />}
        </button>
      </div>

      <div className="flex flex-col gap-0.5">
        <button type="button" onClick={() => ui.openPalette()} className={nav}>
          <Search size={16} strokeWidth={1.5} aria-hidden="true" />
          {!collapsed && (
            <>
              <span>{t('nav.search')}</span>
              <span className="ml-auto opacity-70">
                <Kbd>Ctrl K</Kbd>
              </span>
            </>
          )}
        </button>
        <Link to="/w/$workspace/my-issues" params={{ workspace }} className={nav} activeProps={active}>
          <Inbox size={16} strokeWidth={1.5} aria-hidden="true" />
          {!collapsed && t('nav.myIssues')}
        </Link>
        <Link to="/w/$workspace/plan" params={{ workspace }} className={nav} activeProps={active}>
          <CalendarRange size={16} strokeWidth={1.5} aria-hidden="true" />
          {!collapsed && t('nav.plan')}
        </Link>
        <Link to="/w/$workspace/sprints" params={{ workspace }} className={nav} activeProps={active}>
          <IterationCw size={16} strokeWidth={1.5} aria-hidden="true" />
          {!collapsed && t('nav.sprints')}
        </Link>
      </div>

      <div className="flex min-h-0 flex-1 flex-col gap-0.5 overflow-auto">
        {!collapsed && <div className="px-2 pb-1 text-xs font-semibold tracking-wide uppercase opacity-60">{t('nav.projects')}</div>}
        {projects?.map((p) => (
          <Link
            key={p.id}
            to="/w/$workspace/p/$project"
            params={{ workspace, project: p.key }}
            search={{ view: 'list' }}
            className={nav}
            activeProps={active}
            title={p.name}
          >
            <span className={`size-2.5 shrink-0 rounded-[2px] ${projectColorClass[p.color]}`} aria-hidden="true" />
            {!collapsed && <span className="truncate">{p.name}</span>}
            {!collapsed && <span className="ml-auto font-mono text-xs opacity-60">{p.key}</span>}
          </Link>
        ))}
        <Link to="/w/$workspace/new-project" params={{ workspace }} className={`${nav} opacity-80`}>
          <Plus size={16} strokeWidth={1.5} aria-hidden="true" />
          {!collapsed && t('nav.newProject')}
        </Link>
      </div>

      <div className="flex flex-col gap-0.5">
        {workspaces && workspaces.length > 1 && !collapsed && (
          <div className="flex flex-col gap-0.5">
            {workspaces
              .filter((w) => w.slug !== workspace)
              .map((w) => (
                <Link key={w.id} to="/w/$workspace" params={{ workspace: w.slug }} className={nav}>
                  <Layers size={16} strokeWidth={1.5} aria-hidden="true" />
                  <span className="truncate">{w.name}</span>
                </Link>
              ))}
          </div>
        )}
        <Link to="/w/$workspace/members" params={{ workspace }} className={nav} activeProps={active}>
          <Users size={16} strokeWidth={1.5} aria-hidden="true" />
          {!collapsed && t('nav.members')}
        </Link>
        <Link to="/settings" className={nav}>
          <CircleUser size={16} strokeWidth={1.5} aria-hidden="true" />
          {!collapsed && t('nav.settings')}
        </Link>
        <button type="button" onClick={() => void signOut()} className={nav}>
          <LogOut size={16} strokeWidth={1.5} aria-hidden="true" />
          {!collapsed && t('auth.signOut')}
        </button>
      </div>
    </nav>
  );
}

/** Topbar: breadcrumb, segmented view tabs, one primary button (design guide). */
export function Topbar({ crumbs, tabs, actions }: { crumbs: string[]; tabs?: React.ReactNode; actions?: React.ReactNode }) {
  const { t } = useTranslation();
  const ui = useUi();
  return (
    <header className="flex h-[var(--topbar)] shrink-0 items-center gap-4 border-b border-line px-4">
      <h1 className="flex min-w-0 items-center gap-1.5 text-base">
        {crumbs.map((c, i) => (
          <span key={i} className={`truncate ${i === crumbs.length - 1 ? 'font-semibold' : 'text-mute'}`}>
            {i > 0 && <span className="mr-1.5 text-mute">/</span>}
            {c}
          </span>
        ))}
      </h1>
      {tabs}
      <div className="ml-auto flex items-center gap-2">{actions}</div>
      <button
        type="button"
        onClick={() => ui.setCreateOpen(true)}
        className="inline-flex h-[var(--control)] items-center gap-2 rounded-r1 bg-acc px-3 font-semibold whitespace-nowrap text-on-acc hover:brightness-110"
      >
        <Plus size={16} strokeWidth={1.5} aria-hidden="true" />
        {t('issue.new')}
        <kbd className="border-on-acc/40 text-on-acc/80">C</kbd>
      </button>
    </header>
  );
}
