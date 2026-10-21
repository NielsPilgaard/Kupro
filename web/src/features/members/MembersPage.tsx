import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useParams } from '@tanstack/react-router';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import type { MemberResponse, WorkspaceRole } from '../../api/gen/types.gen';
import {
  deactivateMemberMutation,
  getWorkspaceOptions,
  getWorkspaceQueryKey,
  inviteMemberMutation,
  listMembersOptions,
  listMembersQueryKey,
  updateMemberMutation,
} from '../../api/gen/@tanstack/react-query.gen';
import { Topbar } from '../../app/AppShell';
import { Avatar, Button } from '../../components/ui';
import { useDate } from '../../lib/format';

const roles: WorkspaceRole[] = ['owner', 'member', 'guest'];

/** Problem details the API answers member changes with (`code` plus the guest cap's numbers). */
type Problem = { code?: string; guests?: number; guestLimit?: number } | null;

/** `/w/:workspace/members`: who is in the workspace. Owners invite, change roles and billing access, and deactivate (task 018). */
export function MembersPage() {
  const { t } = useTranslation();
  const { workspace } = useParams({ strict: false }) as { workspace: string };
  const path = { workspace };
  const { data: info } = useQuery(getWorkspaceOptions({ path }));
  const { data: members } = useQuery(listMembersOptions({ path }));
  const isOwner = info?.role === 'owner';
  const people = members?.filter((m) => m.kind === 'human') ?? [];
  const active = people.filter((m) => m.status === 'active');
  const agents = members?.filter((m) => m.kind === 'agent' && m.status === 'active') ?? [];
  const invited = people.filter((m) => m.status === 'invited');
  const deactivated = members?.filter((m) => m.status === 'deactivated') ?? [];

  return (
    <>
      <Topbar crumbs={[t('members.title')]} />
      <main className="mx-auto flex w-full max-w-3xl flex-col gap-8 overflow-auto p-6">
        {info && (
          <p className="flex gap-6 text-sm text-mute" data-testid="member-counts">
            <span>{t('members.seats', { count: info.seats })}</span>
            <span>
              {info.guestLimit == null
                ? t('members.guests', { count: info.guests })
                : t('members.guestsOfLimit', { count: info.guests, limit: info.guestLimit })}
            </span>
          </p>
        )}
        {isOwner && <InviteForm workspace={workspace} />}
        <MemberList title={t('members.people')} members={active} workspace={workspace} isOwner={isOwner} />
        {agents.length > 0 && <MemberList title={t('members.agents')} members={agents} workspace={workspace} isOwner={isOwner} />}
        {isOwner && invited.length > 0 && (
          <MemberList title={t('members.invited')} members={invited} workspace={workspace} isOwner={isOwner} />
        )}
        {isOwner && deactivated.length > 0 && (
          <MemberList title={t('members.deactivated')} members={deactivated} workspace={workspace} isOwner={isOwner} />
        )}
      </main>
    </>
  );
}

/** Refreshes the list and the counts after any change. */
function useRefresh(workspace: string) {
  const qc = useQueryClient();
  const path = { workspace };
  return () => {
    void qc.invalidateQueries({ queryKey: listMembersQueryKey({ path }) });
    void qc.invalidateQueries({ queryKey: getWorkspaceQueryKey({ path }) });
  };
}

function errorText(t: ReturnType<typeof useTranslation>['t'], error: Problem) {
  switch (error?.code) {
    case 'guest_limit_reached':
      return t('members.errors.guestLimit', { count: error.guests, limit: error.guestLimit });
    case 'already_member':
      return t('members.errors.alreadyMember');
    case 'last_owner':
      return t('members.errors.lastOwner');
    default:
      return t('members.errors.failed');
  }
}

function InviteForm({ workspace }: { workspace: string }) {
  const { t } = useTranslation();
  const refresh = useRefresh(workspace);
  const [email, setEmail] = useState('');
  const [role, setRole] = useState<WorkspaceRole>('member');
  const [billing, setBilling] = useState(false);
  const invite = useMutation({
    ...inviteMemberMutation(),
    onSuccess: () => {
      setEmail('');
      setBilling(false);
    },
    onSettled: refresh,
  });

  return (
    <form
      className="flex flex-col gap-3 rounded-r2 border border-line p-4"
      onSubmit={(e: FormEvent) => {
        e.preventDefault();
        if (email.trim()) invite.mutate({ path: { workspace }, body: { email: email.trim(), role, canManageBilling: billing } });
      }}
    >
      <h2 className="text-lg font-semibold">{t('members.invite')}</h2>
      <label className="flex flex-col gap-1">
        <span className="text-sm font-semibold">{t('members.email')}</span>
        <input
          type="email"
          required
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          className="h-[var(--control)] rounded-r1 border border-line bg-panel px-2"
        />
      </label>
      <fieldset className="flex flex-col gap-1">
        <legend className="mb-1 text-sm font-semibold">{t('members.role')}</legend>
        {roles.map((r) => (
          <label key={r} className="flex cursor-pointer items-start gap-2 rounded-r1 px-2 py-1 hover:bg-sel">
            <input type="radio" name="role" value={r} checked={role === r} onChange={() => setRole(r)} className="mt-1" />
            <span className="flex flex-col">
              <span className="font-semibold">{t(`role.${r}`)}</span>
              <span className="text-sm text-mute">
                {t(`role.${r}Hint`)} {t(r === 'guest' ? 'members.noSeat' : 'members.takesSeat')}
              </span>
            </span>
          </label>
        ))}
      </fieldset>
      <label className="flex items-start gap-2 px-2">
        <input type="checkbox" checked={billing} onChange={(e) => setBilling(e.target.checked)} className="mt-1" />
        <span className="flex flex-col">
          <span className="font-semibold">{t('members.billing')}</span>
          <span className="text-sm text-mute">{t('members.billingHint')}</span>
        </span>
      </label>
      {invite.isError && (
        <p role="alert" className="text-sm text-bad">
          {errorText(t, invite.error as Problem)}
        </p>
      )}
      <Button variant="primary" type="submit" disabled={invite.isPending || !email.trim()} className="self-start">
        {t('members.sendInvite')}
      </Button>
    </form>
  );
}

function MemberList(props: { title: string; members: MemberResponse[]; workspace: string; isOwner: boolean }) {
  return (
    <section className="flex flex-col gap-2">
      <h2 className="text-lg font-semibold">{props.title}</h2>
      <ul className="flex flex-col">
        {props.members.map((m) => (
          <MemberRow key={m.id} member={m} workspace={props.workspace} isOwner={props.isOwner} />
        ))}
      </ul>
    </section>
  );
}

function MemberRow({ member, workspace, isOwner }: { member: MemberResponse; workspace: string; isOwner: boolean }) {
  const { t } = useTranslation();
  const date = useDate();
  const refresh = useRefresh(workspace);
  const [confirming, setConfirming] = useState(false);
  const update = useMutation({ ...updateMemberMutation(), onSettled: refresh });
  const deactivate = useMutation({ ...deactivateMemberMutation(), onSuccess: () => setConfirming(false), onSettled: refresh });
  const path = { workspace, memberId: member.id };
  const human = member.kind === 'human';
  const editable = isOwner && member.status !== 'deactivated';
  const error = (update.error ?? deactivate.error) as Problem;

  return (
    <li className="flex flex-col gap-1 border-b border-line py-2" data-testid="member-row">
      <div className="flex min-h-[var(--row)] items-center gap-3">
        <Avatar member={member} />
        <span className="flex min-w-0 flex-1 flex-col">
          <span className="truncate font-semibold">{member.name}</span>
          <span className="truncate text-sm text-mute">
            {human
              ? member.email !== member.name
                ? member.email
                : null
              : t('palette.agentFor', { name: member.ownerName })}
            {member.joinedAt && member.status === 'active' && human && (
              <span> · {t('members.joined', { date: date(member.joinedAt) })}</span>
            )}
          </span>
        </span>
        {editable && human ? (
          <select
            value={member.role}
            aria-label={t('members.roleOf', { name: member.name })}
            onChange={(e) => update.mutate({ path, body: { role: e.target.value as WorkspaceRole } })}
            className="h-[var(--control)] rounded-r1 border border-line bg-panel px-2"
          >
            {roles.map((r) => (
              <option key={r} value={r} title={t(`role.${r}Hint`)}>
                {t(`role.${r}`)}
              </option>
            ))}
          </select>
        ) : (
          <span className="text-sm">{t(`role.${member.role}`)}</span>
        )}
        {human && <BillingCell member={member} editable={editable} onToggle={(on) => update.mutate({ path, body: { canManageBilling: on } })} />}
        {editable && (
          <Button variant="ghost" className="h-7 px-2 text-bad" onClick={() => setConfirming(true)}>
            {t('members.deactivate')}
          </Button>
        )}
      </div>
      {confirming && (
        <div role="alertdialog" aria-label={t('members.deactivateTitle', { name: member.name })} className="flex flex-wrap items-center gap-3 rounded-r1 bg-sel p-2 text-sm">
          <span className="flex-1">{t('members.deactivateConfirm', { name: member.name })}</span>
          <Button variant="ghost" onClick={() => setConfirming(false)}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" disabled={deactivate.isPending} onClick={() => deactivate.mutate({ path })}>
            {t('members.deactivate')}
          </Button>
        </div>
      )}
      {error && (
        <p role="alert" className="text-sm text-bad">
          {errorText(t, error)}
        </p>
      )}
    </li>
  );
}

/** Owners have billing access by role; for everyone else an Owner toggles it. */
function BillingCell({ member, editable, onToggle }: { member: MemberResponse; editable: boolean; onToggle(on: boolean): void }) {
  const { t } = useTranslation();
  if (member.role === 'owner') return <span className="w-32 text-sm text-mute">{t('members.billingAlways')}</span>;
  if (!editable) {
    return <span className="w-32 text-sm text-mute">{member.canManageBilling ? t('members.billing') : ''}</span>;
  }
  return (
    <label className="flex w-32 items-center gap-2 text-sm">
      <input type="checkbox" checked={member.canManageBilling} onChange={(e) => onToggle(e.target.checked)} />
      {t('members.billing')}
    </label>
  );
}
