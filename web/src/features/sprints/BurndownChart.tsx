import { useTranslation } from 'react-i18next';
import type { BurndownIdeal, BurndownPoint } from '../../api/gen/types.gen';
import { useDate, useHours } from '../../lib/format';
import { isWeekend, stepPath } from './sprintClose';

const PAD = { left: 34, right: 8, top: 8, bottom: 18 };

/**
 * Hand-rolled SVG like the delivery plan, no chart dependency (design guide: Burndown). Remaining is a step line; scope
 * dashed; the ideal line straight between weekdays. Each chart carries a table for screen readers.
 */
export function BurndownChart({
  title,
  points,
  ideal,
  atStart,
  endDate,
  max,
  compact = false,
}: {
  title: string;
  points: BurndownPoint[];
  ideal?: BurndownIdeal[];
  atStart?: number;
  endDate: string;
  max: number;
  compact?: boolean;
}) {
  const { t } = useTranslation();
  const date = useDate();
  const hours = useHours();
  const width = compact ? 280 : 720;
  const height = compact ? 110 : 220;
  const dayWidth = (width - PAD.left - PAD.right) / Math.max(points.length, 1);
  const x = (edge: number) => PAD.left + edge * dayWidth;
  const y = (value: number) => PAD.top + (1 - value / max) * (height - PAD.top - PAD.bottom);
  const endEdge = points.findIndex((p) => p.date === endDate) + 1;
  const idealPath = ideal && atStart !== undefined ? `M${x(0)} ${y(atStart)}${ideal.map((p, day) => `L${x(day + 1)} ${y(p.hours)}`).join('')}` : '';

  return (
    <figure className="m-0 flex flex-col gap-1">
      <svg viewBox={`0 0 ${width} ${height}`} className="h-auto w-full" role="img" aria-label={title}>
        {points.map((p, day) =>
          isWeekend(p.date) ? <rect key={p.date} x={x(day)} y={PAD.top} width={dayWidth} height={height - PAD.top - PAD.bottom} className="fill-sunk" /> : null,
        )}
        <line x1={x(0)} x2={x(points.length)} y1={y(0)} y2={y(0)} className="stroke-line" />
        <text x={PAD.left - 4} y={y(max) + 4} textAnchor="end" className="fill-mute font-mono text-[10px]">
          {hours(max)}
        </text>
        <text x={PAD.left - 4} y={y(0)} textAnchor="end" className="fill-mute font-mono text-[10px]">
          0
        </text>
        {endEdge > 0 && endEdge < points.length && (
          <line x1={x(endEdge)} x2={x(endEdge)} y1={PAD.top} y2={y(0)} strokeDasharray="2 3" className="stroke-mute" data-testid="end-date" />
        )}
        {idealPath && <path d={idealPath} fill="none" strokeWidth={1} strokeOpacity={0.6} className="stroke-mute" />}
        <path d={stepPath(points.map((p) => p.scope), x, y)} fill="none" strokeWidth={1.5} strokeDasharray="4 3" className="stroke-mute" data-testid="scope-line" />
        <path d={stepPath(points.map((p) => p.remaining), x, y)} fill="none" strokeWidth={2} className="stroke-acc-ink" data-testid="remaining-line" />
        {points.length > 0 && (
          <>
            <text x={x(0)} y={height - 5} className="fill-mute font-mono text-[10px]">
              {date(points[0]?.date)}
            </text>
            <text x={x(points.length)} y={height - 5} textAnchor="end" className="fill-mute font-mono text-[10px]">
              {date(points[points.length - 1]?.date)}
            </text>
          </>
        )}
      </svg>
      <table className="sr-only">
        <caption>{title}</caption>
        <thead>
          <tr>
            <th scope="col">{t('sprintClose.chart.date')}</th>
            <th scope="col">{t('sprintClose.chart.scope')}</th>
            <th scope="col">{t('sprintClose.chart.remaining')}</th>
            {ideal && <th scope="col">{t('sprintClose.chart.ideal')}</th>}
          </tr>
        </thead>
        <tbody>
          {points.map((p, day) => (
            <tr key={p.date}>
              <th scope="row">{date(p.date)}</th>
              <td>{p.scope == null ? '' : hours(p.scope)}</td>
              <td>{p.remaining == null ? '' : hours(p.remaining)}</td>
              {ideal && <td>{hours(ideal[day]?.hours ?? 0)}</td>}
            </tr>
          ))}
        </tbody>
      </table>
    </figure>
  );
}
