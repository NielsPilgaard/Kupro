import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Plus, X } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { setProjectCapacityMutation } from '../../api/gen/@tanstack/react-query.gen';
import type { CapacityEntry } from '../../api/gen/types.gen';
import { Button } from '../../components/ui';
import { useToast } from '../../components/Toasts';

/** "3 people from Jan · 5 from Jul", edited in place in the row header (docs/delivery-plan.md). */
export function CapacityEditor({ workspace, projectKey, capacity }: { workspace: string; projectKey: string; capacity: CapacityEntry[] }) {
  const { t, i18n } = useTranslation();
  const qc = useQueryClient();
  const toast = useToast();
  const [steps, setSteps] = useState<{ month: string; people: string }[] | null>(null);
  const month = new Intl.DateTimeFormat(i18n.language, { month: 'short', year: '2-digit', timeZone: 'UTC' });
  const number = new Intl.NumberFormat(i18n.language, { maximumFractionDigits: 1 });
  const save = useMutation({
    ...setProjectCapacityMutation(),
    onSuccess: () => {
      setSteps(null);
      void qc.invalidateQueries({ predicate: (q) => (q.queryKey[0] as { _id?: string })._id === 'getPlan' });
    },
    onError: () => toast.error(t('plan.capacityFailed')),
  });

  const summary = capacity.length
    ? capacity.map((c, i) => t(i === 0 ? 'plan.peopleFrom' : 'plan.moreFrom', { count: c.people, people: number.format(c.people), month: month.format(new Date(c.fromMonth)) })).join(' · ')
    : t('plan.noCapacity');

  if (!steps) {
    return (
      <button
        type="button"
        onClick={() => setSteps(capacity.map((c) => ({ month: c.fromMonth.slice(0, 7), people: String(c.people) })))}
        className="truncate rounded-r1 text-left text-xs text-mute hover:text-ink hover:underline"
        title={t('plan.editCapacity')}
      >
        {summary}
      </button>
    );
  }

  const field = 'h-6 rounded-r1 border border-line bg-panel px-1 font-mono text-xs';
  return (
    <form
      className="absolute top-2 left-2 z-30 flex w-64 flex-col gap-1.5 rounded-r2 border border-line bg-panel p-2 shadow-float"
      aria-label={t('plan.editCapacity')}
      onSubmit={(e) => {
        e.preventDefault();
        const entries = steps.filter((s) => s.month && s.people !== '').map((s) => ({ fromMonth: `${s.month}-01`, people: Number(s.people.replace(',', '.')) }));
        save.mutate({ path: { workspace, key: projectKey }, body: { entries } });
      }}
      onKeyDown={(e) => e.key === 'Escape' && setSteps(null)}
    >
      <span className="text-xs font-semibold">{t('plan.capacityFor', { key: projectKey })}</span>
      {steps.map((s, i) => (
        <div key={i} className="flex items-center gap-1">
          <input
            inputMode="decimal"
            value={s.people}
            aria-label={t('plan.people')}
            onChange={(e) => setSteps(steps.map((x, j) => (j === i ? { ...x, people: e.target.value } : x)))}
            className={`${field} w-12`}
          />
          <span className="text-xs text-mute">{t('plan.from')}</span>
          <input
            type="month"
            value={s.month}
            aria-label={t('plan.fromMonth')}
            onChange={(e) => setSteps(steps.map((x, j) => (j === i ? { ...x, month: e.target.value } : x)))}
            className={`${field} flex-1`}
          />
          <button type="button" onClick={() => setSteps(steps.filter((_, j) => j !== i))} className="text-mute hover:text-bad" aria-label={t('plan.removeStep')}>
            <X size={14} strokeWidth={1.5} />
          </button>
        </div>
      ))}
      <button type="button" onClick={() => setSteps([...steps, { month: '', people: '' }])} className="flex items-center gap-1 text-xs text-acc-ink">
        <Plus size={12} strokeWidth={1.5} /> {t('plan.addStep')}
      </button>
      <div className="flex justify-end gap-1">
        <Button variant="ghost" className="h-6 px-2 text-xs" onClick={() => setSteps(null)}>
          {t('common.cancel')}
        </Button>
        <Button variant="primary" type="submit" className="h-6 px-2 text-xs">
          {t('plan.save')}
        </Button>
      </div>
    </form>
  );
}
