import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type {
  CsvColumns,
  CsvPreview,
  FoldEnd,
  HierarchySample,
  ImportMapping,
  ImportPlan,
  IssueState,
  IssueType,
  MemberResponse,
  PersonTarget,
  ProjectResponse,
  SampleKuproNode,
  SourceDescription,
} from '../../api/gen/types.gen';
import { Button, StatusRing, TypeTag } from '../../components/ui';
import { Wordmark } from '../../components/Wordmark';
import { useHours } from '../../lib/format';
import { states } from '../../lib/issues';
import { Chip, OkLine, SourceMark, TableCard, WarnLine } from './parts';
import { control, useSourceName } from './sources';
import { formatBytes, matchOf, peopleCounts, type Decisions, type StepId } from './wizard';

export interface StepProps {
  mapping: ImportMapping;
  update(fn: (m: ImportMapping) => ImportMapping): void;
  description: SourceDescription;
  plan?: ImportPlan;
  members: MemberResponse[];
  projects: ProjectResponse[];
  decided: Decisions;
  decide(key: keyof Decisions, value: boolean): void;
  go(step: StepId): void;
  steps: StepId[];
  open: StepId[];
}

export function StepHead({ title, lede }: { title: string; lede?: ReactNode }) {
  return (
    <div className="flex flex-col gap-1">
      <h1 id="step-heading" tabIndex={-1} className="font-display text-xl font-bold outline-none">
        {title}
      </h1>
      {lede && <p className="text-mute">{lede}</p>}
    </div>
  );
}

function useNumber() {
  const { i18n } = useTranslation();
  const format = new Intl.NumberFormat(i18n.language);
  return (n: number) => format.format(n);
}

/** Samples a plan count carries, as "e.g. WEB-12, WEB-40". */
function Examples({ samples }: { samples?: { key: string | null; title: string; sourceId: string }[] }) {
  const { t } = useTranslation();
  if (!samples?.length) return null;
  return <span className="text-sm text-mute"> {t('importWizard.examples', { list: samples.map((s) => s.key ?? s.title).join(', ') })}</span>;
}

export function ProjectsStep({ mapping, update, description, projects }: StepProps) {
  const { t } = useTranslation();
  const scope = mapping.scope?.projects ?? [];
  const all = description.projects ?? [];
  const inScope = (id: string) => scope.length === 0 || scope.includes(id);
  const toggle = (id: string, on: boolean) =>
    update((m) => {
      const current = all.map((p) => p.id).filter(inScope);
      const next = on ? [...current, id] : current.filter((x) => x !== id);
      return { ...m, scope: { ...m.scope, projects: next.length === all.length ? [] : next } };
    });
  const target = (id: string) => {
    const p = all.find((x) => x.id === id)!;
    return mapping.projects?.[id] ?? { key: p.key ?? '', name: p.name };
  };
  const setTarget = (id: string, value: { key: string; name: string; existing?: boolean }) =>
    update((m) => ({ ...m, projects: { ...m.projects, [id]: value } }));

  return (
    <>
      <StepHead title={t('importWizard.projectsTitle')} lede={t('importWizard.projectsLede')} />
      <TableCard>
        <thead>
          <tr>
            <th>
              <span className="sr-only">{t('importWizard.col.include')}</span>
            </th>
            <th>{t('importWizard.col.project')}</th>
            <th>{t('importWizard.col.process')}</th>
            <th>{t('importWizard.col.into')}</th>
            <th>{t('importWizard.col.key')}</th>
          </tr>
        </thead>
        <tbody>
          {all.map((p) => {
            const tgt = target(p.id);
            const on = inScope(p.id);
            const onlyOne = on && all.filter((x) => inScope(x.id)).length === 1;
            return (
              <tr key={p.id}>
                <td className="w-8">
                  <input
                    type="checkbox"
                    checked={on}
                    disabled={onlyOne}
                    onChange={(e) => toggle(p.id, e.target.checked)}
                    aria-label={t('importWizard.importProject', { name: p.name })}
                  />
                </td>
                <td>{p.name}</td>
                <td className="text-mute">{p.process ?? ''}</td>
                <td>
                  <select
                    className={control}
                    value={tgt.existing ? tgt.key : ''}
                    disabled={!on}
                    aria-label={t('importWizard.intoFor', { name: p.name })}
                    onChange={(e) => {
                      const existing = projects.find((x) => x.key === e.target.value);
                      setTarget(p.id, existing ? { key: existing.key, name: existing.name, existing: true } : { key: p.key ?? '', name: p.name });
                    }}
                  >
                    <option value="">{t('importWizard.newProject')}</option>
                    {projects.map((x) => (
                      <option key={x.id} value={x.key}>
                        {x.name} ({x.key})
                      </option>
                    ))}
                  </select>
                </td>
                <td>
                  <input
                    className={`${control} w-24 font-mono`}
                    value={tgt.key}
                    disabled={!on || !!tgt.existing}
                    maxLength={10}
                    aria-label={t('importWizard.keyFor', { name: p.name })}
                    onChange={(e) => setTarget(p.id, { ...tgt, key: e.target.value.toUpperCase().replace(/[^A-Z0-9]/g, '') })}
                  />
                </td>
              </tr>
            );
          })}
        </tbody>
      </TableCard>
    </>
  );
}

/** Source levels, highest first, each with the type names the source ranks there. */
function levels(description: SourceDescription) {
  const byLevel = new Map<number, string[]>();
  for (const type of description.itemTypes ?? []) {
    if (type.level == null) continue;
    const names = byLevel.get(type.level) ?? [];
    if (!names.includes(type.name)) names.push(type.name);
    byLevel.set(type.level, names);
  }
  return [...byLevel.entries()].sort((a, b) => b[0] - a[0]).slice(0, 4).map(([, names]) => names.join(', '));
}

export function HierarchyStep({ mapping, update, description, plan, decided, decide }: StepProps) {
  const { t } = useTranslation();
  const n = useNumber();
  const chosen = decided.fold ? mapping.fold : undefined;
  const names = levels(description);
  const targets: Record<FoldEnd, string[]> = {
    bottom: [t('type.epic'), t('type.feature'), t('type.task'), t('importWizard.foldTaskUnder')],
    top: [t('importWizard.foldEpicLabel'), t('type.epic'), t('type.feature'), t('type.task')],
  };
  const pick = (fold: FoldEnd) => {
    decide('fold', true);
    update((m) => ({ ...m, fold }));
  };

  return (
    <>
      <StepHead title={t('importWizard.hierarchyTitle')} lede={t('importWizard.hierarchyLede')} />
      <fieldset className="grid min-w-0 grid-cols-2 gap-2 max-[720px]:grid-cols-1">
        <legend className="pb-2 text-md font-semibold">{t('importWizard.foldLegend')}</legend>
        {(['bottom', 'top'] as const).map((fold) => {
          const counts = fold === 'bottom' ? plan?.foldBottom : plan?.foldTop;
          return (
            <label
              key={fold}
              className="flex min-w-0 cursor-pointer flex-col gap-2 rounded-r2 border border-line bg-panel p-3 hover:border-acc-ink has-checked:border-acc-ink has-checked:shadow-[inset_0_0_0_1px_var(--acc-ink)] has-focus-visible:outline-2 has-focus-visible:outline-offset-1 has-focus-visible:outline-acc-ink"
            >
              <span className="flex items-center gap-2">
                <input type="radio" name="fold" value={fold} checked={chosen === fold} onChange={() => pick(fold)} className="focus-visible:outline-none" />
                <b>{t(`importWizard.${fold === 'bottom' ? 'foldBottom' : 'foldTop'}`)}</b>
              </span>
              <span className="grid grid-cols-[auto_14px_1fr] gap-x-1.5 gap-y-0.5 text-sm">
                {targets[fold].map((to, i) => (
                  <span key={i} className="contents">
                    <span>{names[i] ?? t('importWizard.level', { n: i + 1 })}</span>
                    <span className="text-mute">→</span>
                    <span className={(fold === 'bottom' && i === 3) || (fold === 'top' && i === 0) ? 'font-semibold text-rev' : ''}>{to}</span>
                  </span>
                ))}
              </span>
              {counts && (
                <span className="font-mono text-sm">
                  {t('importWizard.foldCounts', { reparented: n(counts.reparented), promoted: n(counts.promoted), folded: n(counts.foldedTops) })}
                </span>
              )}
              <span className="text-sm text-mute">{t(`importWizard.${fold === 'bottom' ? 'foldBottomTrade' : 'foldTopTrade'}`)}</span>
            </label>
          );
        })}
      </fieldset>
      {plan?.sample && plan.sample.source.length > 0 && <HierarchyTrees sample={plan.sample} fold={chosen} kind={description.source.kind} />}
    </>
  );
}

/** Indent guides, one per level (prototype 07). */
function Guides({ depth }: { depth: number }) {
  return Array.from({ length: depth }, (_, i) => <span key={i} aria-hidden="true" className="ml-2.5 w-3.5 shrink-0 self-stretch border-l border-line" />);
}

const nodeRow =
  'flex h-[var(--row)] min-w-0 items-center gap-1.5 border-b border-line/55 pr-2 pl-1.5 last:border-b-0 outline-none focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-acc-ink';
const twin = 'bg-sel shadow-[inset_2px_0_0_var(--acc-ink)]';

/** Real branches in the source and where they land under the chosen fold. Hover or focus an item to find its twin. */
function HierarchyTrees({ sample, fold, kind }: { sample: HierarchySample; fold?: FoldEnd; kind: string }) {
  const { t } = useTranslation();
  const sourceName = useSourceName();
  const [hover, setHover] = useState<string | null>(null);
  const landed = fold === 'bottom' ? sample.bottom : fold === 'top' ? sample.top : undefined;
  const byId = new Map(landed?.map((n) => [n.sourceId, n]));
  const sourceKey = (id: string) => sample.source.find((x) => x.sourceId === id)?.key ?? `#${id}`;
  const tops = new Set(landed?.flatMap((n) => (n.labelOf ? [n.labelOf] : [])));
  const note = (n: SampleKuproNode) =>
    n.labelOf
      ? t('importWizard.treeLabel', { name: byId.get(n.labelOf)?.title ?? sourceKey(n.labelOf) })
      : tops.has(n.sourceId)
        ? t('importWizard.treeTop')
        : n.oldParent
          ? t('importWizard.treeMoved', { key: byId.get(n.oldParent)?.key ?? sourceKey(n.oldParent) })
          : n.promoted
            ? t('importWizard.treePromoted')
            : null;
  const twinProps = (id: string) => ({
    tabIndex: 0,
    onMouseEnter: () => setHover(id),
    onMouseLeave: () => setHover(null),
    onFocus: () => setHover(id),
    onBlur: () => setHover(null),
  });
  const pane = 'flex min-w-0 flex-col overflow-hidden rounded-r2 border border-line bg-panel';
  const head = 'flex h-[46px] items-center gap-2 border-b border-line bg-sunk px-2.5';
  const inSource = t('importWizard.treeIn', { name: sourceName(kind) });

  return (
    <div className="@container flex flex-col gap-2">
      <div className="grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)] gap-2 @max-[920px]:grid-cols-1">
        <section className={pane} aria-label={inSource}>
          <header className={head}>
            <SourceMark kind={kind} />
            <b className="truncate">{inSource}</b>
          </header>
          <ul>
            {sample.source.map((n) => {
              const landedAs = byId.get(n.sourceId);
              return (
                <li key={n.sourceId} className={`${nodeRow} ${hover === n.sourceId ? twin : ''}`} {...twinProps(n.sourceId)}>
                  <Guides depth={n.depth} />
                  {landedAs && <StatusRing state={landedAs.state} />}
                  <span className="min-w-[66px] shrink-0 font-mono text-sm whitespace-nowrap text-mute">{n.key ?? `#${n.sourceId}`}</span>
                  <span className="inline-flex h-4 shrink-0 items-center rounded-r1 border border-line px-1 text-sm font-semibold text-mute">{n.type}</span>
                  <span className="min-w-[30%] flex-1 truncate" title={n.title}>
                    {n.title}
                  </span>
                </li>
              );
            })}
          </ul>
        </section>
        <section className={pane} aria-label={t('importWizard.treeInKupro')}>
          <header className={head}>
            <Wordmark size="sm" />
          </header>
          {landed ? (
            <ul>
              {landed.map((n) => {
                const text = note(n);
                return (
                  <li key={n.sourceId} className={`${nodeRow} ${hover === n.sourceId ? twin : ''}`} {...twinProps(n.sourceId)}>
                    <Guides depth={n.depth} />
                    <StatusRing state={n.state} />
                    <span className="min-w-[66px] shrink-0 font-mono text-sm whitespace-nowrap text-mute">{n.key ?? '—'}</span>
                    <TypeTag type={n.type} />
                    <span className="min-w-[30%] flex-1 truncate" title={n.title}>
                      {n.title}
                    </span>
                    {text && (
                      <span className="max-w-[45%] min-w-18 shrink truncate rounded-r1 bg-warn/14 px-1.5 text-sm text-ink" title={text}>
                        {text}
                      </span>
                    )}
                  </li>
                );
              })}
            </ul>
          ) : (
            <p className="my-auto px-4 py-6 text-center text-mute">{t('importWizard.treePick')}</p>
          )}
        </section>
      </div>
      <p className="text-sm text-mute">{t('importWizard.treeLegend')}</p>
    </div>
  );
}

const kuproTypes: IssueType[] = ['epic', 'feature', 'task'];

export function TypesStep({ mapping, update, description }: StepProps) {
  const { t } = useTranslation();
  const skipped = mapping.scope?.skipTypes ?? [];
  const set = (id: string, value: IssueType | 'skip') =>
    update((m) => {
      const skip = (m.scope?.skipTypes ?? []).filter((x) => x !== id);
      if (value === 'skip') return { ...m, scope: { ...m.scope, skipTypes: [...skip, id] } };
      const current = m.types?.[id];
      return { ...m, scope: { ...m.scope, skipTypes: skip }, types: { ...m.types, [id]: { ...current, type: value } } };
    });
  const setLabel = (id: string, label: string) =>
    update((m) => ({ ...m, types: { ...m.types, [id]: { type: m.types?.[id]?.type ?? 'task', ...m.types?.[id], label: label.trim() || null } } }));

  return (
    <>
      <StepHead title={t('importWizard.typesTitle')} lede={t('importWizard.typesLede')} />
      <TableCard>
        <thead>
          <tr>
            <th>{t('importWizard.col.type')}</th>
            <th>{t('importWizard.col.project')}</th>
            <th>{t('importWizard.col.kuproType')}</th>
            <th>{t('importWizard.col.label')}</th>
          </tr>
        </thead>
        <tbody>
          {(description.itemTypes ?? []).map((type) => {
            const target = mapping.types?.[type.id];
            const value = skipped.includes(type.id) ? 'skip' : (target?.type ?? 'task');
            return (
              <tr key={type.id}>
                <td>{type.name}</td>
                <td className="text-mute">{(type.project && description.projects?.find((p) => p.id === type.project)?.name) ?? ''}</td>
                <td>
                  <span className="flex items-center gap-2">
                    <select
                      className={control}
                      value={value}
                      aria-label={t('importWizard.typeFor', { name: type.name })}
                      onChange={(e) => set(type.id, e.target.value as IssueType | 'skip')}
                    >
                      {kuproTypes.map((k) => (
                        <option key={k} value={k}>
                          {t(`type.${k}`)}
                        </option>
                      ))}
                      <option value="skip">{t('importWizard.skip')}</option>
                    </select>
                    {value !== 'skip' && <TypeTag type={value} />}
                  </span>
                </td>
                <td>
                  <input
                    className={`${control} w-32`}
                    defaultValue={target?.label ?? ''}
                    disabled={value === 'skip'}
                    aria-label={t('importWizard.labelFor', { name: type.name })}
                    onBlur={(e) => e.target.value.trim() !== (target?.label ?? '') && setLabel(type.id, e.target.value)}
                  />
                </td>
              </tr>
            );
          })}
        </tbody>
      </TableCard>
    </>
  );
}

export function StatesStep({ mapping, update, description }: StepProps) {
  const { t } = useTranslation();
  const toReview = mapping.statusesToReview ?? [];
  const set = (id: string, state: IssueState) =>
    update((m) => ({ ...m, states: { ...m.states, [id]: state }, statusesToReview: (m.statusesToReview ?? []).filter((x) => x !== id) }));
  const typeName = (id?: string | null) => (id ? (description.itemTypes?.find((x) => x.id === id)?.name ?? '') : '');

  return (
    <>
      <StepHead title={t('importWizard.statesTitle')} lede={t('importWizard.statesLede')} />
      <TableCard>
        <thead>
          <tr>
            <th>{t('importWizard.col.state')}</th>
            <th>{t('importWizard.col.category')}</th>
            <th>{t('importWizard.col.kuproState')}</th>
          </tr>
        </thead>
        <tbody>
          {(description.statuses ?? []).map((s) => {
            const value = mapping.states?.[s.id] ?? 'todo';
            return (
              <tr key={s.id}>
                <td>
                  {s.name}
                  {s.itemType && <span className="ml-2 text-sm text-mute">{typeName(s.itemType)}</span>}
                </td>
                <td className="text-mute">{s.category ?? ''}</td>
                <td>
                  <span className="flex items-center gap-2">
                    <StatusRing state={value} />
                    <select className={control} value={value} aria-label={t('importWizard.stateFor', { name: s.name })} onChange={(e) => set(s.id, e.target.value as IssueState)}>
                      {states.map((k) => (
                        <option key={k} value={k}>
                          {t(`state.${k}`)}
                        </option>
                      ))}
                    </select>
                    {toReview.includes(s.id) && <Chip tone="warn">{t('importWizard.checkThis')}</Chip>}
                  </span>
                </td>
              </tr>
            );
          })}
        </tbody>
      </TableCard>
    </>
  );
}

export function PeopleStep({ mapping, update, description, members }: StepProps) {
  const { t } = useTranslation();
  const humans = members.filter((m) => m.kind === 'human' && m.status === 'active');
  const set = (id: string, value: string) => {
    const target: PersonTarget = value.startsWith('m:')
      ? { action: 'member', memberId: value.slice(2) }
      : { action: value as PersonTarget['action'], memberId: null };
    update((m) => ({ ...m, people: { ...m.people, [id]: target } }));
  };
  const matchTone = { email: 'ok', name: 'warn', none: 'neutral', bot: 'neutral' } as const;

  return (
    <>
      <StepHead title={t('importWizard.peopleTitle')} lede={t('importWizard.peopleLede')} />
      <TableCard>
        <thead>
          <tr>
            <th>{t('importWizard.col.person')}</th>
            <th>{t('importWizard.col.match')}</th>
            <th>{t('importWizard.col.inKupro')}</th>
          </tr>
        </thead>
        <tbody>
          {(description.people ?? []).map((p) => {
            const target = mapping.people?.[p.id];
            const match = matchOf(p, target, members);
            const value = target?.action === 'member' ? `m:${target.memberId}` : (target?.action ?? 'placeholder');
            return (
              <tr key={p.id}>
                <td>
                  <span className="flex flex-col py-1">
                    <span>{p.name}</span>
                    {p.email && <span className="text-sm text-mute">{p.email}</span>}
                  </span>
                </td>
                <td>
                  <Chip tone={matchTone[match]}>{t(`importWizard.match.${match}`)}</Chip>
                </td>
                <td>
                  <select className={`${control} max-w-72`} value={value} aria-label={t('importWizard.personFor', { name: p.name })} onChange={(e) => set(p.id, e.target.value)}>
                    {humans.map((m) => (
                      <option key={m.id} value={`m:${m.id}`}>
                        {m.name}
                      </option>
                    ))}
                    <option value="placeholder">{t('importWizard.asPlaceholder', { name: p.name })}</option>
                    <option value="invite" disabled={!p.email}>
                      {p.email ? t('importWizard.invite', { email: p.email }) : t('importWizard.inviteNoEmail')}
                    </option>
                    <option value="skip">{t('importWizard.skipPerson')}</option>
                  </select>
                </td>
              </tr>
            );
          })}
        </tbody>
      </TableCard>
    </>
  );
}

export function FieldsStep({ mapping, update, description }: StepProps) {
  const { t } = useTranslation();
  const labelFields = mapping.labelFields ?? [];
  const fields = description.fields ?? [];
  const toggle = (id: string, on: boolean) =>
    update((m) => ({ ...m, labelFields: on ? [...(m.labelFields ?? []), id] : (m.labelFields ?? []).filter((x) => x !== id) }));

  return (
    <>
      <StepHead title={t('importWizard.fieldsTitle')} lede={t('importWizard.fieldsLede')} />
      {fields.length === 0 ? (
        <p className="text-mute">{t('importWizard.noFields')}</p>
      ) : (
        <TableCard>
          <thead>
            <tr>
              <th>
                <span className="sr-only">{t('importWizard.col.include')}</span>
              </th>
              <th>{t('importWizard.col.field')}</th>
              <th>{t('importWizard.col.becomes')}</th>
            </tr>
          </thead>
          <tbody>
            {fields.map((f) => {
              const selectable = f.kind === 'select' || f.kind === 'multi-select';
              const on = labelFields.includes(f.id);
              return (
                <tr key={f.id}>
                  <td className="w-8">
                    {selectable && (
                      <input type="checkbox" checked={on} onChange={(e) => toggle(f.id, e.target.checked)} aria-label={t('importWizard.useField', { name: f.name })} />
                    )}
                  </td>
                  <td>
                    {f.name}
                    {f.options && f.options.length > 0 && (
                      <span className="ml-2 text-sm text-mute">{t('importWizard.fieldOptions', { list: f.options.slice(0, 3).join(', '), count: f.options.length })}</span>
                    )}
                  </td>
                  <td>{on ? t('importWizard.becomesLabels') : t('importWizard.becomesDetails')}</td>
                </tr>
              );
            })}
          </tbody>
        </TableCard>
      )}
    </>
  );
}

export function EffortStep({ mapping, update, plan, decided, decide }: StepProps) {
  const { t } = useTranslation();
  const hours = useHours();
  const noEffort = decided.effort && mapping.hoursPerPoint == null;
  return (
    <>
      <StepHead title={t('importWizard.effortTitle')} lede={t('importWizard.effortLede')} />
      <label className="flex flex-col gap-1">
        <span className="text-sm font-semibold">{t('importWizard.hoursPerPoint')}</span>
        <input
          // Uncontrolled: remount when "no effort" toggles, so the box shows the saved value, not the last typed one.
          key={String(noEffort)}
          className={`${control} w-32 font-mono`}
          inputMode="decimal"
          placeholder="6"
          disabled={noEffort}
          defaultValue={mapping.hoursPerPoint ?? ''}
          onChange={(e) => {
            const value = Number(e.target.value.replace(',', '.'));
            const valid = e.target.value.trim() !== '' && Number.isFinite(value) && value > 0;
            decide('effort', valid);
            update((m) => ({ ...m, hoursPerPoint: valid ? value : null }));
          }}
        />
        <small className="text-sm text-mute">{t('importWizard.hoursHint')}</small>
      </label>
      <label className="flex items-center gap-2">
        <input
          type="checkbox"
          checked={noEffort}
          onChange={(e) => {
            decide('effort', e.target.checked);
            if (e.target.checked) update((m) => ({ ...m, hoursPerPoint: null }));
          }}
        />
        {t('importWizard.noEffort')}
      </label>
      {plan && <p className="text-sm text-mute">{t('importWizard.effortPlanned', { hours: hours(plan.effortHours) })}</p>}
    </>
  );
}

export function ReviewStep({ mapping, plan, decided, go, steps, description }: StepProps) {
  const { t, i18n } = useTranslation();
  const n = useNumber();
  const hours = useHours();
  const people = peopleCounts(mapping);
  const scope = mapping.scope?.projects ?? [];
  const keys = (description.projects ?? [])
    .filter((p) => scope.length === 0 || scope.includes(p.id))
    .map((p) => mapping.projects?.[p.id]?.key ?? p.key ?? p.name);
  const rows: { step: StepId; label: string; value: string; ok: boolean }[] = [
    ...(steps.includes('hierarchy')
      ? [{ step: 'hierarchy' as const, label: t('importWizard.steps.hierarchy'), value: decided.fold && mapping.fold ? t(`importWizard.${mapping.fold === 'bottom' ? 'foldBottom' : 'foldTop'}`) : t('importWizard.notChosen'), ok: decided.fold }]
      : []),
    {
      step: 'effort',
      label: t('importWizard.steps.effort'),
      value:
        mapping.hoursPerPoint != null
          ? t('importWizard.perPoint', { hours: hours(mapping.hoursPerPoint) })
          : mapping.csv && !mapping.csv.points
            ? t('importWizard.hoursAsIs')
            : decided.effort
              ? t('importWizard.withoutEffort')
              : t('importWizard.perPointNotSet'),
      ok: decided.effort || !!(mapping.csv && !mapping.csv.points),
    },
    { step: 'people', label: t('importWizard.steps.people'), value: t('importWizard.peopleSummary', people), ok: true },
    { step: 'projects', label: t('importWizard.steps.projects'), value: keys.join(', '), ok: !plan?.projectKeysTaken?.length && !plan?.projectsMissing?.length },
  ];
  const warnings = plan
    ? ([
        ['statusesToReview', plan.statusesToReview],
        ['keysTaken', plan.keysTaken],
        ['conflicts', plan.conflicts],
        ['deletedInSource', plan.deletedInSource],
        ['parentLoops', plan.parentLoops],
        ['skippedByScope', plan.skippedByScope],
      ] as const).filter(([, c]) => c && c.count > 0)
    : [];

  return (
    <>
      <StepHead title={t('importWizard.reviewTitle')} lede={t('importWizard.reviewLede')} />
      <div className="grid grid-cols-[110px_1fr_auto] items-center gap-x-3 gap-y-1.5 rounded-r2 border border-line bg-panel p-4">
        {rows.map((r) => (
          <span key={r.step} className="contents">
            <span className="font-semibold">{r.label}</span>
            {r.ok ? <span className="min-w-0 truncate">{r.value}</span> : <WarnLine>{r.value}</WarnLine>}
            <Button variant="ghost" className="h-6 px-2 text-sm" onClick={() => go(r.step)}>
              {t('importWizard.change')}
            </Button>
          </span>
        ))}
      </div>
      {plan && (
        <>
          <div className="grid grid-cols-4 rounded-r2 border border-line bg-panel max-sm:grid-cols-2">
            {[
              [n(plan.create.count), t('importWizard.count.issues')],
              [steps.includes('hierarchy') && !decided.fold ? '—' : n(plan.reparented.count), t('importWizard.count.reparented')],
              [n(people.placeholders), t('importWizard.count.placeholders')],
              [formatBytes(plan.attachmentBytes, i18n.language), t('importWizard.count.attachments')],
            ].map(([value, label]) => (
              <div key={label} className="flex flex-col gap-0.5 border-r border-line px-3 py-2 last:border-r-0">
                <b className="font-mono text-xl font-normal tabular-nums">{value}</b>
                <span className="text-sm text-mute">{label}</span>
              </div>
            ))}
          </div>
          {(plan.effortByProject?.length ?? 0) > 0 && (
            <TableCard>
              <thead>
                <tr>
                  <th>{t('importWizard.col.project')}</th>
                  <th className="text-right!">{t('importWizard.col.effort')}</th>
                </tr>
              </thead>
              <tbody>
                {plan.effortByProject!.map((p) => (
                  <tr key={p.project}>
                    <td className="font-mono">{p.project}</td>
                    <td className="text-right font-mono tabular-nums">{hours(p.hours)}</td>
                  </tr>
                ))}
              </tbody>
            </TableCard>
          )}
          {warnings.length > 0 && (
            <div className="flex flex-col gap-1">
              {warnings.map(([key, c]) => (
                <WarnLine key={key}>
                  {t(`importWizard.warn.${key}`, { count: c!.count })}
                  <Examples samples={c!.samples} />
                </WarnLine>
              ))}
            </div>
          )}
          {warnings.length === 0 && <OkLine>{t('importWizard.noWarnings')}</OkLine>}
          <p className="text-sm text-mute">
            {t('importWizard.alsoCopied', {
              comments: n(plan.comments),
              changes: n(plan.changes),
              sprints: n(plan.sprintsToCreate ?? 0),
              labels: n(plan.labelsToCreate ?? 0),
            })}
          </p>
        </>
      )}
    </>
  );
}

const csvFields = [
  'title',
  'key',
  'description',
  'type',
  'status',
  'priority',
  'assignee',
  'parent',
  'labels',
  'estimateHours',
  'points',
  'start',
  'due',
  'created',
  'project',
] as const;

/** The column step of a CSV source: which column holds what. Known exports (Jira, Linear) come pre-mapped. */
export function CsvColumnsTable({
  columns,
  preview,
  onChange,
}: {
  columns: CsvColumns;
  preview?: CsvPreview;
  onChange(columns: CsvColumns): void;
}) {
  const { t } = useTranslation();
  const headers = preview?.headers ?? [...new Set(csvFields.map((f) => columns[f]).filter((h): h is string => typeof h === 'string' && !!h))];
  const sample = (header: string | null | undefined) => {
    const at = header && preview ? preview.headers.indexOf(header) : -1;
    return at >= 0 ? (preview!.samples[0]?.[at] ?? '') : '';
  };

  return (
    <div className="flex flex-col gap-2">
      <h2 className="text-lg font-semibold">{t('importWizard.csvColumns')}</h2>
      {preview && (
        <p className="text-sm text-mute">
          {t('importWizard.csvLede', { rows: preview.rows, encoding: preview.encoding, delimiter: preview.delimiter })}{' '}
          {preview.columns.preset ? t('importWizard.csvPreset', { preset: preview.columns.preset }) : t('importWizard.csvGuess')}
        </p>
      )}
      <TableCard>
        <thead>
          <tr>
            <th>{t('importWizard.col.kuproField')}</th>
            <th>{t('importWizard.col.column')}</th>
            <th>{t('importWizard.col.sample')}</th>
          </tr>
        </thead>
        <tbody>
          {csvFields.map((field) => {
            const value = (columns[field] as string | null | undefined) ?? '';
            return (
              <tr key={field}>
                <td>{t(`importWizard.csvField.${field}`)}</td>
                <td>
                  <select
                    className={`${control} max-w-56`}
                    value={value}
                    aria-label={t('importWizard.columnFor', { name: t(`importWizard.csvField.${field}`) })}
                    onChange={(e) => onChange({ ...columns, [field]: e.target.value || null })}
                  >
                    <option value="">{t('importWizard.csvNone')}</option>
                    {headers.map((h) => (
                      <option key={h} value={h}>
                        {h}
                      </option>
                    ))}
                  </select>
                </td>
                <td className="max-w-48 truncate text-sm text-mute">{sample(value)}</td>
              </tr>
            );
          })}
        </tbody>
      </TableCard>
    </div>
  );
}
