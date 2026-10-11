import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type { IssuePriority, IssueState, IssueType, MemberRef } from '../api/gen/types.gen';

const stateColor: Record<IssueState, string> = {
  backlog: 'text-mute',
  todo: 'text-ink',
  inProgress: 'text-prog',
  inReview: 'text-rev',
  done: 'text-ok',
  canceled: 'text-mute',
};

/** 14 px ring; the shape encodes the state, the name is the label (design guide). */
export function StatusRing({ state, className = '' }: { state: IssueState; className?: string }) {
  const { t } = useTranslation();
  const c = 7;
  const r = 5.6;
  return (
    <svg
      width="14"
      height="14"
      viewBox="0 0 14 14"
      role="img"
      aria-label={t(`state.${state}`)}
      className={`shrink-0 ${stateColor[state]} ${className}`}
    >
      {state === 'backlog' && (
        <circle cx={c} cy={c} r={r} fill="none" stroke="currentColor" strokeWidth="1.6" strokeDasharray="2.2 1.8" />
      )}
      {(state === 'todo' || state === 'inProgress' || state === 'inReview' || state === 'canceled') && (
        <circle cx={c} cy={c} r={r} fill="none" stroke="currentColor" strokeWidth="1.6" />
      )}
      {state === 'inProgress' && <path d={`M${c} ${c - 3.2}A3.2 3.2 0 0 1 ${c} ${c + 3.2}Z`} fill="currentColor" />}
      {state === 'inReview' && (
        <path d={`M${c} ${c}V${c - 3.2}A3.2 3.2 0 1 1 ${c - 3.2} ${c}Z`} fill="currentColor" />
      )}
      {state === 'done' && (
        <>
          <circle cx={c} cy={c} r={6.4} fill="currentColor" />
          <path d="M4.3 7.2l1.8 1.8 3.6-3.8" fill="none" className="stroke-panel" strokeWidth="1.6" strokeLinecap="round" />
        </>
      )}
      {state === 'canceled' && (
        <path d="M5 5l4 4M9 5l-4 4" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
      )}
    </svg>
  );
}

/** Epic/Feature tag; tasks stay untagged so the common case is quiet. */
export function TypeTag({ type }: { type: IssueType }) {
  const { t } = useTranslation();
  if (type === 'task') return null;
  const tone = type === 'epic' ? 'text-rev bg-rev/14' : 'text-acc-ink bg-acc-ink/14';
  return (
    <span className={`inline-flex h-4 shrink-0 items-center rounded-r1 px-1 text-sm font-semibold ${tone}`}>
      {t(`type.${type}`)}
    </span>
  );
}

export function LabelPill({ name }: { name: string }) {
  const dot = name === 'bug' ? 'bg-bad' : 'bg-mute';
  return (
    <span className="inline-flex h-[19px] max-w-32 shrink-0 items-center gap-1.5 rounded-full border border-line px-2 text-xs text-mute">
      <span className={`size-1.5 shrink-0 rounded-full ${dot}`} aria-hidden="true" />
      <span className="truncate">{name}</span>
    </span>
  );
}

/** At most two labels, then +N (design guide). */
export function Labels({ names }: { names: string[] }) {
  if (names.length === 0) return null;
  return (
    <span className="flex items-center gap-1">
      {names.slice(0, 2).map((n) => (
        <LabelPill key={n} name={n} />
      ))}
      {names.length > 2 && <span className="text-xs text-mute">+{names.length - 2}</span>}
    </span>
  );
}

const avatarTones = ['bg-proj-verdigris', 'bg-proj-copper', 'bg-proj-plum', 'bg-proj-moss', 'bg-proj-fjord', 'bg-proj-slate'];

/** Humans: initials in a circle. Agents: rounded square with >_ (design guide). Unassigned: dashed circle. */
export function Avatar({ member, size = 22 }: { member: MemberRef | null | undefined; size?: number }) {
  const { t } = useTranslation();
  const box = { width: size, height: size };
  if (!member) {
    return (
      <span
        style={box}
        className="inline-block shrink-0 rounded-full border border-dashed border-line"
        role="img"
        aria-label={t('issue.unassigned')}
      />
    );
  }

  if (member.kind === 'agent') {
    return (
      <span
        style={box}
        title={member.name}
        role="img"
        aria-label={t('issue.agent', { name: member.name })}
        className="inline-flex shrink-0 items-center justify-center rounded-[6px] bg-ink font-mono text-[9px] text-bg"
      >
        &gt;_
      </span>
    );
  }

  const initials = member.name
    .split(/\s+/)
    .map((p) => p[0])
    .join('')
    .slice(0, 2)
    .toUpperCase();
  const tone = avatarTones[[...member.id].reduce((h, ch) => (h * 31 + ch.charCodeAt(0)) >>> 0, 7) % avatarTones.length];
  return (
    <span
      style={box}
      title={member.name}
      role="img"
      aria-label={member.name}
      className={`inline-flex shrink-0 items-center justify-center rounded-full text-[9px] font-semibold text-on-acc ${tone}`}
    >
      {initials}
    </span>
  );
}

const priorityBars: Record<IssuePriority, number> = { none: 0, low: 1, medium: 2, high: 3, urgent: 4 };

export function PriorityIcon({ priority }: { priority: IssuePriority }) {
  const { t } = useTranslation();
  const level = priorityBars[priority];
  if (priority === 'urgent') {
    return (
      <span role="img" aria-label={t('priority.urgent')} className="inline-flex size-3.5 items-center justify-center rounded-[3px] bg-bad text-[9px] font-bold text-on-acc">
        !
      </span>
    );
  }

  return (
    <svg width="14" height="14" viewBox="0 0 14 14" role="img" aria-label={t(`priority.${priority}`)} className="shrink-0 text-mute">
      {[0, 1, 2].map((i) => (
        <rect
          key={i}
          x={2 + i * 4}
          y={10 - i * 3}
          width="2.6"
          height={3 + i * 3}
          rx="0.6"
          fill="currentColor"
          opacity={i < level ? 1 : 0.25}
        />
      ))}
    </svg>
  );
}

export function Kbd({ children }: { children: ReactNode }) {
  return <kbd>{children}</kbd>;
}

type Variant = 'primary' | 'secondary' | 'ghost';

const variants: Record<Variant, string> = {
  primary: 'bg-acc text-on-acc font-semibold hover:brightness-110',
  secondary: 'bg-panel border border-line text-ink hover:bg-sel',
  ghost: 'text-mute hover:bg-sel hover:text-ink',
};

export function Button({ variant = 'secondary', className = '', ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant }) {
  return (
    <button
      type="button"
      {...props}
      className={`inline-flex h-[var(--control)] items-center gap-2 rounded-r1 px-3 whitespace-nowrap transition-colors duration-[var(--dur-1)] disabled:opacity-50 ${variants[variant]} ${className}`}
    />
  );
}
