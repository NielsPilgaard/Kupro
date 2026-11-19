import { PackageOpen } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type { ImportProjectSummary } from '../../api/gen/types.gen';
import { sourceOf } from './sources';
import type { SparkBar } from './wizard';

/** Product mark in a 30 px tile (40 px as a heading mark); formats without a vendor use a Lucide icon. */
export function SourceMark({ kind, large = false }: { kind: string; large?: boolean }) {
  const mark = sourceOf(kind)?.mark ?? PackageOpen;
  const tile = large ? 'size-10 rounded-r2' : 'size-[30px] rounded-r1';
  const size = large ? 24 : 18;
  return (
    <span aria-hidden="true" className={`inline-flex shrink-0 items-center justify-center border border-line bg-panel text-ink ${tile}`}>
      {typeof mark === 'string' ? (
        <img src={mark} alt="" width={size} height={size} />
      ) : (
        (() => {
          const Icon = mark;
          return <Icon size={size} strokeWidth={1.5} />;
        })()
      )}
    </span>
  );
}

type Tone = 'ok' | 'warn' | 'bad' | 'neutral' | 'acc' | 'wip';

const chipTone: Record<Tone, string> = {
  ok: 'text-ink bg-ok/14',
  warn: 'text-ink bg-warn/14',
  bad: 'text-bad bg-bad/14',
  neutral: 'text-mute bg-mute/14',
  acc: 'text-acc-ink bg-sel',
  wip: 'text-mute border border-line tracking-wider',
};

const chipSymbol: Partial<Record<Tone, ReactNode>> = {
  ok: <span className="text-ok">✓</span>,
  warn: <span className="font-bold text-warn">!</span>,
  bad: <span>×</span>,
};

/** 20 px status chip with a symbol first, so it reads without colour (design guide: status chips). */
export function Chip({ tone = 'neutral', children, className = '' }: { tone?: Tone; children: ReactNode; className?: string }) {
  return (
    <span className={`inline-flex h-5 shrink-0 items-center gap-1 rounded-r1 px-1.5 text-xs font-semibold ${chipTone[tone]} ${className}`}>
      {chipSymbol[tone]}
      {children}
    </span>
  );
}

/** A warning line: a 14 px warn square holding `!`, text in ink. */
export function WarnLine({ children, className = '' }: { children: ReactNode; className?: string }) {
  return (
    <span className={`flex items-start gap-1.5 text-ink ${className}`}>
      <span
        aria-hidden="true"
        className="mt-0.5 inline-flex size-3.5 shrink-0 items-center justify-center rounded-[3px] bg-warn text-[10px] font-bold text-panel"
      >
        !
      </span>
      <span className="min-w-0">{children}</span>
    </span>
  );
}

export function OkLine({ children, className = '' }: { children: ReactNode; className?: string }) {
  return (
    <span className={`flex items-start gap-1.5 ${className}`}>
      <span aria-hidden="true" className="shrink-0 font-bold text-ok">
        ✓
      </span>
      <span className="min-w-0">{children}</span>
    </span>
  );
}

/** 6 px bar on a sunk track (design guide: progress). */
export function ProgressBar({ value }: { value: number }) {
  return (
    <div className="h-1.5 overflow-hidden rounded-[3px] bg-sunk">
      <i className="block h-full rounded-[3px] bg-acc transition-[width] duration-[var(--dur-2)]" style={{ width: `${Math.min(1, Math.max(0, value)) * 100}%` }} />
    </div>
  );
}

/** Phase list under a progress bar: done in ok, current in accent and 600, next as a ring. */
export function Phases({ phases, current }: { phases: { id: string; label: string }[]; current: number }) {
  return (
    <ol className="flex flex-col gap-1 text-sm">
      {phases.map((p, i) => (
        <li key={p.id} className={`flex items-center gap-2 ${i < current ? 'text-ink' : i === current ? 'font-semibold text-ink' : 'text-mute'}`}>
          <span
            aria-hidden="true"
            className={`size-2 shrink-0 rounded-full border-[1.5px] ${i < current ? 'border-ok bg-ok' : i === current ? 'border-acc bg-acc' : 'border-line'}`}
          />
          {p.label}
        </li>
      ))}
    </ol>
  );
}

/** 30 px high, one 9 px bar per run: created (accent), updated (mute), failed (bad). */
export function Sparkline({ bars }: { bars: SparkBar[] }) {
  const { t } = useTranslation();
  if (bars.length === 0) return null;
  const max = Math.max(1, ...bars.map((b) => b.created + b.updated));
  const text = (b: SparkBar) => t('imports.sparkRun', { n: b.n, created: b.created, updated: b.updated, failed: b.failed ? 1 : 0 });
  return (
    <div role="img" aria-label={`${t('imports.sparkline')}. ${bars.map(text).join('. ')}`} className="flex h-[30px] items-end gap-0.5">
      {bars.map((b) => (
        <span key={b.n} title={text(b)} className="flex h-full w-[9px] flex-col-reverse overflow-hidden rounded-[2px] bg-sunk">
          {b.failed ? (
            <i className="block h-1/3 bg-bad" />
          ) : (
            <>
              {b.created > 0 && <i className="block bg-acc" style={{ height: `${(b.created / max) * 100}%` }} />}
              {b.updated > 0 && <i className="block bg-mute/55" style={{ height: `${(b.updated / max) * 100}%` }} />}
            </>
          )}
        </span>
      ))}
    </div>
  );
}

/** Card holding a data table: header on sunk, rows `--row` high (design guide: data table). */
export function TableCard({ children }: { children: ReactNode }) {
  return (
    <div className="overflow-x-auto rounded-r2 border border-line bg-panel">
      <table className="w-full border-collapse [&_tbody_tr:hover_td]:bg-acc/6 [&_tbody_tr:last-child_td]:border-b-0 [&_td]:h-[var(--row)] [&_td]:border-b [&_td]:border-line [&_td]:px-3 [&_td]:align-middle [&_th]:h-[30px] [&_th]:border-b [&_th]:border-line [&_th]:bg-sunk [&_th]:px-3 [&_th]:text-left [&_th]:text-sm [&_th]:font-semibold [&_th]:whitespace-nowrap [&_th]:text-mute">
        {children}
      </table>
    </div>
  );
}


/** A commit's report per project: issues imported so far, re-parented by the fit, conflicts open. */
export function ProjectSummary({ projects }: { projects: ImportProjectSummary[] }) {
  const { t, i18n } = useTranslation();
  const number = new Intl.NumberFormat(i18n.language);
  if (projects.length === 0) return null;
  const cell = 'text-right font-mono tabular-nums';
  return (
    <TableCard>
      <thead>
        <tr>
          <th>{t('importRuns.perProject.project')}</th>
          <th className="text-right!">{t('importRuns.perProject.imported')}</th>
          <th className="text-right!">{t('importRuns.perProject.reparented')}</th>
          <th className="text-right!">{t('importRuns.perProject.conflicts')}</th>
        </tr>
      </thead>
      <tbody>
        {projects.map((p) => (
          <tr key={p.project}>
            <td className="font-mono">{p.project}</td>
            <td className={cell}>{number.format(p.imported)}</td>
            <td className={cell}>{number.format(p.reparented)}</td>
            <td className={`${cell} ${p.conflicts ? 'text-bad' : ''}`}>{number.format(p.conflicts)}</td>
          </tr>
        ))}
      </tbody>
    </TableCard>
  );
}
