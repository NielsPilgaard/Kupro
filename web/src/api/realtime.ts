import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { useQuery, useQueryClient, type Query } from '@tanstack/react-query';
import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { getAccessToken } from '../auth/auth';
import { useToast } from '../components/Toasts';
import { getIssueQueryKey, getMeOptions, listIssueActivityQueryKey } from './gen/@tanstack/react-query.gen';
import type { IssuePage, IssueSummary, Surface } from './gen/types.gen';
import { isIssueList, useMembers } from './issues';

/** Server pushes from the workspace hub (task 012); shapes mirror WorkspaceHub.cs. */
interface IssuesEvent {
  items: IssueSummary[];
  created: boolean;
  actorId: string;
  via: Surface;
}

interface ChangedEvent {
  topic: 'issue' | 'sprints' | 'plan';
  issueKey: string | null;
  actorId: string;
  via: Surface;
}

const HUB = '/api/v1/realtime';
const idOf = (q: Query) => (q.queryKey[0] as { _id?: string } | undefined)?._id ?? '';
const byOperation = (...ids: string[]) => ({ predicate: (q: Query) => ids.includes(idOf(q)) });

/**
 * Keeps the open workspace live: changed issues patch every cached list in place, other changes refetch what they
 * touch. The server wins: when someone else changes the issue open in the panel, a toast says who and how.
 */
export function useRealtime(workspace: string, openIssue: string | undefined) {
  const qc = useQueryClient();
  const toast = useToast();
  const { t } = useTranslation();
  const { data: members } = useMembers(workspace);
  const { data: me } = useQuery(getMeOptions());

  // Handlers read the latest values through a ref, so the connection isn't rebuilt when they change.
  const latest = useRef({ members, me, openIssue, t, toast });
  useEffect(() => {
    latest.current = { members, me, openIssue, t, toast };
  });

  useEffect(() => {
    const isMe = (actorId: string) => {
      const { members, me } = latest.current;
      return members?.some((m) => m.id === actorId && m.kind === 'human' && m.email === me?.email) ?? false;
    };
    const touched = (key: string, actorId: string, via: Surface) => {
      void qc.invalidateQueries({ queryKey: getIssueQueryKey({ path: { workspace, key } }) });
      void qc.invalidateQueries({ queryKey: listIssueActivityQueryKey({ path: { workspace, key } }) });
      const { members, openIssue, t, toast } = latest.current;
      const actor = members?.find((m) => m.id === actorId);
      if (key === openIssue && actor && !isMe(actorId)) {
        toast.info(t('realtime.changed', { key, name: actor.name, via: t(`feed.surface.${via}`) }));
      }
    };

    const connection = new HubConnectionBuilder()
      .withUrl(HUB, { accessTokenFactory: async () => (await getAccessToken()) ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('issues', (e: IssuesEvent) => {
      const changed = new Map(e.items.map((i) => [i.id, i]));
      qc.setQueriesData<IssuePage>({ predicate: isIssueList }, (page) =>
        page ? { ...page, items: page.items.map((i) => changed.get(i.id) ?? i) } : page,
      );
      // Own changes already refetch through the mutation; others may move issues in or out of filtered lists.
      if (e.created || !isMe(e.actorId)) void qc.invalidateQueries({ predicate: isIssueList });
      for (const item of e.items) touched(item.key, e.actorId, e.via);
      void qc.invalidateQueries(byOperation('getPlan', 'listSprints', 'getSprint'));
    });

    connection.on('changed', (e: ChangedEvent) => {
      if (e.topic === 'issue' && e.issueKey) touched(e.issueKey, e.actorId, e.via);
      if (e.topic === 'sprints') void qc.invalidateQueries(byOperation('listSprints', 'getSprint', 'getPlan'));
      if (e.topic === 'plan') void qc.invalidateQueries(byOperation('getPlan', 'listMilestones', 'getProjectCapacity'));
    });

    // Events missed while disconnected are gone; refetch everything that is on screen.
    connection.onreconnected(() => {
      void connection.invoke('Join', workspace);
      void qc.invalidateQueries();
    });

    connection
      .start()
      .then(() => connection.invoke('Join', workspace))
      .catch(() => {
        // Offline or stopped while starting (React strict mode): the app works without push, queries still refetch.
      });
    return () => void connection.stop();
  }, [workspace, qc]);
}
