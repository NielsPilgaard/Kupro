import type { CapacityEntry, PlanCard, PlanMonth } from '../../api/gen/types.gen';

/** Days since epoch for an ISO date (UTC), so date math never trips over time zones or DST. */
export const dayOf = (iso: string) => Date.UTC(+iso.slice(0, 4), +iso.slice(5, 7) - 1, +iso.slice(8, 10)) / 86_400_000;
export const isoOf = (day: number) => new Date(day * 86_400_000).toISOString().slice(0, 10);
export const daysInMonth = (month: string) => new Date(Date.UTC(+month.slice(0, 4), +month.slice(5, 7), 0)).getUTCDate();
export const lastDayOf = (month: string) => `${month.slice(0, 8)}${String(daysInMonth(month)).padStart(2, '0')}`;

export function monthsBetween(from: string, to: string): string[] {
  const months: string[] = [];
  let y = +from.slice(0, 4);
  let m = +from.slice(5, 7);
  while (`${y}-${String(m).padStart(2, '0')}-01` <= to) {
    months.push(`${y}-${String(m).padStart(2, '0')}-01`);
    m = m === 12 ? 1 : m + 1;
    if (m === 1) y++;
  }
  return months;
}

/** Hours one person gives in a month: hours per week × days in month / 7 (ADR-008). */
export const hoursPerPerson = (month: string, hoursPerWeek: number) => (hoursPerWeek * daysInMonth(month)) / 7;

const round = (v: number) => Math.round(v * 100) / 100;

/**
 * Per-month demand and supply for one project. Mirror of PlanService.Month on the server, so the band can update live
 * while a card is dragged (each card's counted effort spread evenly over its days).
 */
export function monthsFor(
  months: string[],
  cards: Pick<PlanCard, 'countedEffort' | 'startDate' | 'dueDate'>[],
  capacity: CapacityEntry[],
  hoursPerWeek: number,
): PlanMonth[] {
  return months.map((month) => {
    const start = dayOf(month);
    const end = dayOf(lastDayOf(month));
    let demand = 0;
    for (const card of cards) {
      if (!card.countedEffort || !card.startDate || !card.dueDate) continue;
      const s = dayOf(card.startDate);
      const d = dayOf(card.dueDate);
      if (d < s) continue;
      const overlap = Math.min(d, end) - Math.max(s, start) + 1;
      if (overlap > 0) demand += (card.countedEffort * overlap) / (d - s + 1);
    }

    const people = [...capacity].reverse().find((c) => c.fromMonth <= month)?.people ?? 0;
    const perPerson = hoursPerPerson(month, hoursPerWeek);
    const demandPeople = round(demand / perPerson);
    return {
      month,
      demandHours: round(demand),
      supplyHours: round(people * perPerson),
      demandPeople,
      supplyPeople: people,
      gapPeople: round(demandPeople - people),
    };
  });
}

export function sumMonths(rows: PlanMonth[][], months: string[], hoursPerWeek: number): PlanMonth[] {
  return months.map((month, i) => {
    const demand = rows.reduce((s, r) => s + (r[i]?.demandHours ?? 0), 0);
    const supply = rows.reduce((s, r) => s + (r[i]?.supplyHours ?? 0), 0);
    const per = hoursPerPerson(month, hoursPerWeek);
    const demandPeople = round(demand / per);
    const supplyPeople = round(supply / per);
    return { month, demandHours: round(demand), supplyHours: round(supply), demandPeople, supplyPeople, gapPeople: round(demandPeople - supplyPeople) };
  });
}

/** Maps dates to pixels with every month the same width, so cards line up with the month grid. */
export function timeline(months: string[], monthWidth: number) {
  const first = months[0]!;
  const index = (iso: string) => (+iso.slice(0, 4) - +first.slice(0, 4)) * 12 + (+iso.slice(5, 7) - +first.slice(5, 7));
  const x = (iso: string) => index(iso) * monthWidth + ((+iso.slice(8, 10) - 1) / daysInMonth(iso)) * monthWidth;
  const xEnd = (iso: string) => x(iso) + monthWidth / daysInMonth(iso);
  const dateAt = (px: number) => {
    const i = Math.max(0, Math.min(months.length - 1, Math.floor(px / monthWidth)));
    const month = months[i]!;
    const day = Math.min(daysInMonth(month), Math.max(1, Math.floor(((px - i * monthWidth) / monthWidth) * daysInMonth(month)) + 1));
    return `${month.slice(0, 8)}${String(day).padStart(2, '0')}`;
  };
  return { x, xEnd, dateAt, width: months.length * monthWidth };
}

/** Greedy interval packing: each dated card gets the first lane that is free by its start. */
export function lanes<T extends { key: string; startDate: string | null; dueDate: string | null }>(cards: T[]) {
  const ends: number[] = [];
  const lane = new Map<string, number>();
  for (const card of [...cards].filter((c) => c.startDate && c.dueDate).sort((a, b) => a.startDate!.localeCompare(b.startDate!))) {
    const start = dayOf(card.startDate!);
    let i = ends.findIndex((end) => end < start - 1);
    if (i === -1) i = ends.push(0) - 1;
    ends[i] = dayOf(card.dueDate!);
    lane.set(card.key, i);
  }
  return { lane, count: Math.max(1, ends.length) };
}
