import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import { Check, Mic, MicOff, Pause, Play, ScreenShare, ScreenShareOff, Square, Undo2, X } from 'lucide-react';
import { useEffect, useRef, useState, type FormEvent } from 'react';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { listSprintsOptions, listWorkspacesOptions } from '../../api/gen/@tanstack/react-query.gen';
import type { IssueState, MeetingResponse, MeetingSettingsResponse, ProposalOperation, ProposalResponse } from '../../api/gen/types.gen';
import { useMembers } from '../../api/issues';
import {
  useMeeting,
  useMeetingSettings,
  useProposals,
  useReview,
  useSay,
  useSendAudio,
  useUndo,
  useUpdateMeeting,
  useUpdateMeetingSettings,
} from '../../api/meetings';
import { Button, StatusRing } from '../../components/ui';
import { useToast } from '../../components/Toasts';
import { canCaptureCallAudio, startMicrophone, type Microphone } from '../../lib/microphone';

const states: IssueState[] = ['backlog', 'todo', 'inProgress', 'inReview', 'done', 'canceled'];

/** The meeting agent's panel (task 015): listening controls, proposals grouped by ticket, applied changes with undo. */
export function MeetingPanel({ workspace, meetingId }: { workspace: string; meetingId: string }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { data: meeting, isError } = useMeeting(workspace, meetingId);
  const close = () =>
    void navigate({
      to: '.',
      search: (prev: Record<string, unknown>) => ({
        ...prev,
        meeting: undefined,
      }),
    });

  return (
    <aside
      aria-label={t('meeting.panel')}
      className="flex w-[min(480px,100%)] shrink-0 flex-col border-l border-line bg-panel max-md:fixed max-md:inset-0 max-md:z-30 max-md:w-full"
    >
      <header className="flex h-[var(--topbar)] shrink-0 items-center gap-2 border-b border-line px-4">
        <span className="font-semibold">{meeting ? t(`meeting.type.${meeting.type}`) : t('meeting.panel')}</span>
        {meeting && <span className="font-mono text-sm text-mute">{meeting.project}</span>}
        {meeting && <LiveBadge state={meeting.state} />}
        <button
          type="button"
          onClick={close}
          className="ml-auto rounded-r1 p-1 text-mute hover:bg-sel hover:text-ink"
          aria-label={t('common.close')}
        >
          <X size={16} strokeWidth={1.5} />
        </button>
      </header>
      {isError && <p className="p-4 text-bad">{t('meeting.notFound')}</p>}
      {meeting && <MeetingBody workspace={workspace} meeting={meeting} />}
    </aside>
  );
}

function LiveBadge({ state }: { state: MeetingResponse['state'] }) {
  const { t } = useTranslation();
  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-full px-2 text-xs ${state === 'live' ? 'bg-sel text-acc-ink' : 'bg-sunk text-mute'}`}
    >
      <span
        className={`size-1.5 rounded-full ${state === 'live' ? 'animate-pulse bg-acc motion-reduce:animate-none' : 'bg-mute'}`}
        aria-hidden="true"
      />
      {t(`meeting.state.${state}`)}
    </span>
  );
}

function MeetingBody({ workspace, meeting }: { workspace: string; meeting: MeetingResponse }) {
  const { t } = useTranslation();
  const { data: proposals = [] } = useProposals(workspace, meeting.id);
  const update = useUpdateMeeting(workspace, meeting.id);
  const pending = proposals.filter((p) => p.status === 'pending');
  const applied = proposals.filter((p) => p.status === 'applied' || p.status === 'undone');

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-auto p-4">
      <div className="flex flex-wrap items-center gap-2">
        {meeting.state === 'live' && (
          <Button onClick={() => update({ state: 'paused' })}>
            <Pause size={14} strokeWidth={1.5} aria-hidden="true" />
            {t('meeting.pause')}
          </Button>
        )}
        {meeting.state === 'paused' && (
          <Button onClick={() => update({ state: 'live' })}>
            <Play size={14} strokeWidth={1.5} aria-hidden="true" />
            {t('meeting.resume')}
          </Button>
        )}
        {meeting.state !== 'ended' && (
          <Button variant="ghost" onClick={() => update({ state: 'ended' })}>
            <Square size={12} strokeWidth={1.5} fill="currentColor" aria-hidden="true" />
            {t('meeting.end')}
          </Button>
        )}
      </div>

      {meeting.state === 'ended' ? <Summary meeting={meeting} /> : <ModeBar workspace={workspace} meeting={meeting} />}
      {meeting.state === 'live' && <Listen workspace={workspace} meeting={meeting} />}
      {meeting.state !== 'ended' && <p className="text-xs text-mute">{t('meeting.privacy')}</p>}

      <section aria-label={t('meeting.pending')} className="flex flex-col gap-3">
        <h3 className="text-sm font-semibold text-mute">{t('meeting.pendingCount', { count: pending.length })}</h3>
        {pending.length === 0 && <p className="text-sm text-mute">{t('meeting.nothingPending')}</p>}
        {groupByIssue(pending).map(([key, group]) => (
          <ProposalGroup key={key} workspace={workspace} meeting={meeting} group={group} />
        ))}
      </section>

      {applied.length > 0 && <AppliedFeed workspace={workspace} meeting={meeting} proposals={applied} />}
    </div>
  );
}

function ModeBar({ workspace, meeting }: { workspace: string; meeting: MeetingResponse }) {
  const { t } = useTranslation();
  const { data: settings } = useMeetingSettings(workspace);
  const { data: workspaces } = useQuery(listWorkspacesOptions());
  const isOwner = workspaces?.find((w) => w.slug === workspace)?.role === 'owner';
  const update = useUpdateMeeting(workspace, meeting.id);
  const updateSettings = useUpdateMeetingSettings(workspace);
  const canFullAuto = settings?.fullAutoEnabled ?? false;

  return (
    <div className="flex flex-col gap-2 rounded-r2 border border-line p-3">
      <div role="radiogroup" aria-label={t('meeting.mode')} className="flex w-fit rounded-r1 bg-sunk p-0.5">
        {(['suggest', 'fullAuto'] as const).map((mode) => (
          <button
            key={mode}
            type="button"
            role="radio"
            aria-checked={meeting.mode === mode}
            disabled={mode === 'fullAuto' && !canFullAuto}
            onClick={() => update({ mode })}
            className={`h-6 rounded-[4px] px-3 disabled:opacity-50 ${meeting.mode === mode ? 'bg-panel font-semibold shadow-sm' : 'text-mute hover:text-ink'}`}
          >
            {t(`meeting.modes.${mode}`)}
          </button>
        ))}
      </div>
      <p className="text-xs text-mute">{t(`meeting.modeHelp.${meeting.mode}`)}</p>
      {settings && <TrustStats settings={settings} />}
      {isOwner && settings && (
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={settings.fullAutoEnabled}
            onChange={(e) => updateSettings({ fullAutoEnabled: e.target.checked })}
          />
          {t('meeting.allowFullAuto', {
            ops: settings.fullAutoOperations.map((op) => t(`meeting.op.${op}`).toLowerCase()).join(', '),
          })}
        </label>
      )}
    </div>
  );
}

/** "You approved 47 of 50 status changes unchanged": suggest mode earning trust before full auto (task 015). */
function TrustStats({ settings }: { settings: MeetingSettingsResponse }) {
  const { t } = useTranslation();
  const decided = settings.stats.filter((s) => s.decided > 0);
  if (decided.length === 0) return <p className="text-xs text-mute">{t('meeting.noHistory')}</p>;
  return (
    <ul className="flex flex-col gap-0.5 text-xs text-mute">
      {decided.map((s) => (
        <li key={s.operation}>
          {t('meeting.stat', {
            approved: s.approvedUnchanged,
            decided: s.decided,
            op: t(`meeting.op.${s.operation}`).toLowerCase(),
          })}
        </li>
      ))}
    </ul>
  );
}

function Listen({ workspace, meeting }: { workspace: string; meeting: MeetingResponse }) {
  const { t } = useTranslation();
  const toast = useToast();
  const say = useSay();
  const sendAudio = useSendAudio(workspace, meeting.id);
  const [text, setText] = useState('');
  const [speaking, setSpeaking] = useState(false);
  const [mic, setMic] = useState<Microphone | null>(null);
  const [callAudio, setCallAudio] = useState(false);
  const micRef = useRef<Microphone | null>(null);

  // Turning the panel or the meeting off releases the microphone.
  useEffect(() => () => micRef.current?.stop(), []);

  const toggleMic = async () => {
    if (mic) {
      mic.stop();
      micRef.current = null;
      setMic(null);
      setSpeaking(false);
      setCallAudio(false);
      return;
    }
    try {
      const started = await startMicrophone({
        onUtterance: sendAudio,
        onSpeaking: setSpeaking,
      });
      micRef.current = started;
      setMic(started);
    } catch {
      toast.error(t('meeting.micDenied'));
    }
  };

  const toggleCallAudio = async () => {
    if (!mic) return;
    if (callAudio) {
      mic.removeCallAudio();
      setCallAudio(false);
      return;
    }
    try {
      if (await mic.addCallAudio(() => setCallAudio(false))) setCallAudio(true);
      else toast.error(t('meeting.callNoAudio'));
    } catch {
      // Cancelled in the browser's share picker.
    }
  };

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!text.trim()) return;
    say.mutate({ path: { workspace, id: meeting.id }, body: { text } }, { onSuccess: () => setText('') });
  };

  return (
    <div className="flex flex-col gap-2">
      {meeting.canTranscribe && (
        <Button variant={mic ? 'primary' : 'secondary'} onClick={() => void toggleMic()} aria-pressed={!!mic}>
          {mic ? <MicOff size={14} strokeWidth={1.5} aria-hidden="true" /> : <Mic size={14} strokeWidth={1.5} aria-hidden="true" />}
          {mic ? t('meeting.micOff') : t('meeting.micOn')}
          {mic && <span className="text-xs opacity-80">{speaking ? t('meeting.hearing') : t('meeting.listening')}</span>}
        </Button>
      )}
      {mic && canCaptureCallAudio() && (
        <>
          <Button variant={callAudio ? 'primary' : 'secondary'} onClick={() => void toggleCallAudio()} aria-pressed={callAudio}>
            {callAudio ? (
              <ScreenShareOff size={14} strokeWidth={1.5} aria-hidden="true" />
            ) : (
              <ScreenShare size={14} strokeWidth={1.5} aria-hidden="true" />
            )}
            {callAudio ? t('meeting.callOff') : t('meeting.callOn')}
          </Button>
          {!callAudio && <p className="text-xs text-mute">{t('meeting.callHelp')}</p>}
        </>
      )}
      <form onSubmit={submit} className="flex gap-2">
        <input
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder={t('meeting.typePlaceholder')}
          aria-label={t('meeting.typeLabel')}
          className="h-[var(--control)] min-w-0 flex-1 rounded-r1 border border-line bg-panel px-2"
        />
        <Button type="submit" disabled={say.isPending || !text.trim()}>
          {t('meeting.send')}
        </Button>
      </form>
    </div>
  );
}

function ProposalGroup({ workspace, meeting, group }: { workspace: string; meeting: MeetingResponse; group: ProposalResponse[] }) {
  const { t } = useTranslation();
  const { review, pending } = useReview(workspace, meeting.id);
  const head = group[0]!;
  const approvable = group.filter((p) => !p.needsIssue);

  return (
    <article className="flex flex-col gap-2 rounded-r2 border border-line p-3" aria-label={groupTitle(head, t)}>
      <header className="flex items-center gap-2">
        {head.issue ? (
          <Link
            to="."
            search={(prev: Record<string, unknown>) => ({
              ...prev,
              issue: head.issue!.key,
            })}
            className="flex min-w-0 items-center gap-1.5 hover:underline"
          >
            <StatusRing state={head.issue.state} />
            <span className="font-mono text-sm text-mute">{head.issue.key}</span>
            <span className="truncate">{head.issue.title}</span>
          </Link>
        ) : (
          <span className="font-semibold">{groupTitle(head, t)}</span>
        )}
        {approvable.length > 1 && (
          <Button
            variant="ghost"
            className="ml-auto"
            disabled={pending}
            onClick={() => review(approvable.map((p) => ({ id: p.id, decision: 'approve' })))}
          >
            {t('meeting.approveAll')}
          </Button>
        )}
      </header>
      <ul className="flex flex-col gap-2">
        {group.map((p) => (
          <ProposalItem key={p.id} workspace={workspace} meeting={meeting} proposal={p} />
        ))}
      </ul>
    </article>
  );
}

function ProposalItem({ workspace, meeting, proposal }: { workspace: string; meeting: MeetingResponse; proposal: ProposalResponse }) {
  const { t } = useTranslation();
  const { review, pending } = useReview(workspace, meeting.id);
  const [editing, setEditing] = useState(false);
  const [value, setValue] = useState(proposal.value ?? '');

  return (
    <li className="flex flex-col gap-1.5 border-t border-line pt-2 first:border-t-0 first:pt-0">
      <div className="text-sm">
        <Change proposal={proposal} />
        {proposal.confidence < 0.8 && <span className="ml-2 rounded-full bg-sunk px-1.5 text-xs text-mute">{t('meeting.unsure')}</span>}
      </div>
      {proposal.snippet && <q className="text-sm text-mute italic">{proposal.snippet}</q>}
      {proposal.needsIssue && (
        <div className="flex flex-col gap-1">
          <span className="text-xs text-mute">{proposal.candidates.length > 0 ? t('meeting.whichIssue') : t('meeting.noMatch')}</span>
          {proposal.candidates.map((c) => (
            <button
              key={c.key}
              type="button"
              disabled={pending}
              onClick={() => review([{ id: proposal.id, decision: 'approve', issue: c.key }])}
              className="flex items-center gap-1.5 rounded-r1 px-1 text-left text-sm hover:bg-sel"
            >
              <StatusRing state={c.state} />
              <span className="font-mono text-mute">{c.key}</span>
              <span className="truncate">{c.title}</span>
            </button>
          ))}
        </div>
      )}
      {editing && <ValueEditor workspace={workspace} meeting={meeting} operation={proposal.operation} value={value} onChange={setValue} />}
      <div className="flex gap-1.5">
        {!proposal.needsIssue && (
          <Button
            variant="primary"
            disabled={pending}
            onClick={() =>
              review([
                {
                  id: proposal.id,
                  decision: 'approve',
                  value: editing && value !== proposal.value ? value : null,
                },
              ])
            }
          >
            <Check size={14} strokeWidth={1.5} aria-hidden="true" />
            {editing ? t('meeting.applyEdit') : t('meeting.approve')}
          </Button>
        )}
        {!editing && !proposal.needsIssue && (
          <Button onClick={() => setEditing(true)} disabled={pending}>
            {t('meeting.edit')}
          </Button>
        )}
        <Button variant="ghost" disabled={pending} onClick={() => review([{ id: proposal.id, decision: 'reject' }])}>
          {t('meeting.reject')}
        </Button>
      </div>
    </li>
  );
}

function ValueEditor({
  workspace,
  meeting,
  operation,
  value,
  onChange,
}: {
  workspace: string;
  meeting: MeetingResponse;
  operation: ProposalOperation;
  value: string;
  onChange(v: string): void;
}) {
  const { t } = useTranslation();
  const { data: members } = useMembers(workspace);
  const { data: sprints } = useQuery({
    ...listSprintsOptions({
      path: { workspace },
      query: { project: meeting.project, state: ['planned', 'active'] },
    }),
    enabled: operation === 'sprint',
  });
  const field = 'h-[var(--control)] rounded-r1 border border-line bg-panel px-2';
  const label = t('meeting.newValue');

  switch (operation) {
    case 'state':
      return (
        <select aria-label={label} value={value} onChange={(e) => onChange(e.target.value)} className={field}>
          {states.map((s) => (
            <option key={s} value={s}>
              {t(`state.${s}`)}
            </option>
          ))}
        </select>
      );
    case 'sprint':
      return (
        <select aria-label={label} value={value} onChange={(e) => onChange(e.target.value)} className={field}>
          <option value="none">{t('feed.none')}</option>
          {sprints?.map((s) => (
            <option key={s.id} value={s.id}>
              {s.name}
            </option>
          ))}
        </select>
      );
    case 'assignee':
      return (
        <select aria-label={label} value={value} onChange={(e) => onChange(e.target.value)} className={field}>
          <option value="none">{t('feed.none')}</option>
          {members
            ?.filter((m) => m.status === 'active')
            .map((m) => (
              <option key={m.id} value={m.id}>
                {m.name}
              </option>
            ))}
        </select>
      );
    case 'estimate':
      return (
        <input
          aria-label={label}
          type="number"
          min={0}
          step={0.5}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          className={field}
        />
      );
    default:
      return (
        <textarea aria-label={label} value={value} onChange={(e) => onChange(e.target.value)} rows={2} className={`${field} h-auto py-1`} />
      );
  }
}

/** "Sprint: none → Sprint 15", "State: Todo → Done", "Comment: …", "New issue: …". */
function Change({ proposal }: { proposal: ProposalResponse }) {
  const { t } = useTranslation();
  const show = (v: string | null | undefined) =>
    v == null ? t('feed.none') : proposal.operation === 'state' ? t(`state.${v as IssueState}`) : v;
  if (proposal.operation === 'comment' || proposal.operation === 'createIssue') {
    return (
      <span>
        <span className="font-semibold">{t(`meeting.op.${proposal.operation}`)}:</span> {proposal.valueDisplay}
      </span>
    );
  }
  return (
    <span>
      <span className="font-semibold">{t(`meeting.op.${proposal.operation}`)}:</span> {show(proposal.beforeDisplay)} →{' '}
      {show(proposal.valueDisplay)}
    </span>
  );
}

function AppliedFeed({ workspace, meeting, proposals }: { workspace: string; meeting: MeetingResponse; proposals: ProposalResponse[] }) {
  const { t } = useTranslation();
  const { undo, undoAll } = useUndo(workspace, meeting.id);
  const live = proposals.filter((p) => p.status === 'applied');

  return (
    <section aria-label={t('meeting.applied')} className="flex flex-col gap-2">
      <header className="flex items-center">
        <h3 className="text-sm font-semibold text-mute">{t('meeting.applied')}</h3>
        {live.length > 1 && (
          <Button variant="ghost" className="ml-auto" onClick={() => window.confirm(t('meeting.confirmUndoAll')) && undoAll()}>
            {t('meeting.undoAll')}
          </Button>
        )}
      </header>
      <ul className="flex flex-col gap-1.5">
        {[...proposals].reverse().map((p) => (
          <li key={p.id} className={`flex items-center gap-2 text-sm ${p.status === 'undone' ? 'text-mute line-through' : ''}`}>
            <span className="font-mono text-mute">{p.issue?.key}</span>
            <span className="min-w-0 flex-1 truncate">
              <Change proposal={p} />
            </span>
            <span className="text-xs text-mute">{p.autoApplied ? t('meeting.auto') : p.reviewedBy?.name}</span>
            {p.status === 'applied' && (
              <Button variant="ghost" onClick={() => undo(p.id)} aria-label={t('meeting.undoOne', { key: p.issue?.key })}>
                <Undo2 size={14} strokeWidth={1.5} aria-hidden="true" />
              </Button>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}

function Summary({ meeting }: { meeting: MeetingResponse }) {
  const { t } = useTranslation();
  const c = meeting.counts;
  return (
    <div className="rounded-r2 border border-line p-3 text-sm">
      <p className="font-semibold">{t('meeting.summaryTitle')}</p>
      <p className="text-mute">
        {t('meeting.summary', {
          applied: c.applied,
          rejected: c.rejected,
          pending: c.pending,
          undone: c.undone,
        })}
      </p>
    </div>
  );
}

function groupTitle(p: ProposalResponse, t: TFunction) {
  if (p.issue) return `${p.issue.key} ${p.issue.title}`;
  return p.operation === 'createIssue' ? t('meeting.newIssues') : t('meeting.whichIssueGroup');
}

/** Proposals grouped by ticket, in the order they were first heard; new issues and unresolved ones get their own group. */
function groupByIssue(proposals: ProposalResponse[]): [string, ProposalResponse[]][] {
  const groups = new Map<string, ProposalResponse[]>();
  for (const p of proposals) {
    const key = p.issue?.key ?? (p.operation === 'createIssue' ? '+new' : `?${p.id}`);
    groups.set(key, [...(groups.get(key) ?? []), p]);
  }
  return [...groups];
}
