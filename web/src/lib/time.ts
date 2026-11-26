import { useTranslation } from 'react-i18next';

/**
 * Minutes from what a person types: `1h30m`, `1t30m`, `90m`, `1.5h`, `1,5`, `2` (hours) or `1:30`. Empty is 0;
 * anything else is NaN. Same forms as the CLI's durations (task 034), plus the Danish `t` and bare hours.
 */
export function parseDuration(text: string): number {
  const value = text.trim().toLowerCase().replace(',', '.').replace(/\s+/g, '');
  if (!value) return 0;
  if (/^\d+(?:\.\d+)?$/.test(value)) return Math.round(Number(value) * 60);
  const clock = /^(\d{1,2}):([0-5]\d)$/.exec(value);
  if (clock) return Number(clock[1]) * 60 + Number(clock[2]);
  // `1h30` reads as 1 h 30 m: minutes after hours may drop their unit.
  const units = /^(?:(\d+(?:\.\d+)?)[ht])?(?:(\d+)(?:m|min)?)?$/.exec(value);
  if (!units?.[0]) return NaN;
  return Math.round(Number(units[1] ?? 0) * 60 + Number(units[2] ?? 0));
}

/** "1h 30m" / "1t 30m"; empty for 0. */
export function useDuration() {
  const { t } = useTranslation();
  return (minutes: number | null | undefined) => {
    if (!minutes) return '';
    const h = Math.floor(minutes / 60);
    const m = minutes % 60;
    return h && m ? t('time.unit.hm', { h, m }) : h ? t('time.unit.h', { h }) : t('time.unit.m', { m });
  };
}

/** Today in Europe/Copenhagen, the day the API checks "not later than today" against (ADR-021). */
export function today(now = new Date()): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Copenhagen', year: 'numeric', month: '2-digit', day: '2-digit' }).format(now);
}

const day = (iso: string) => new Date(`${iso}T00:00:00Z`);
const iso = (d: Date) => d.toISOString().slice(0, 10);

export function addDays(date: string, days: number): string {
  const d = day(date);
  d.setUTCDate(d.getUTCDate() + days);
  return iso(d);
}

/** The Monday of the week holding `date`. */
export function mondayOf(date: string): string {
  return addDays(date, -((day(date).getUTCDay() + 6) % 7));
}

type Week = [string, string, string, string, string, string, string];

/** The seven days, Monday to Sunday, from a Monday. */
export const weekDays = (monday: string) => Array.from({ length: 7 }, (_, i) => addDays(monday, i)) as Week;

/** ISO 8601 week number. */
export function isoWeek(date: string): number {
  const d = day(date);
  d.setUTCDate(d.getUTCDate() + 3 - ((d.getUTCDay() + 6) % 7));
  const yearStart = Date.UTC(d.getUTCFullYear(), 0, 1);
  return Math.ceil(((d.getTime() - yearStart) / 86_400_000 + 1) / 7);
}

/** Calendar day labels in the user's language; calendar days format in UTC so they never shift (see useDate). */
export function useDayFormat() {
  const { i18n } = useTranslation();
  const weekday = new Intl.DateTimeFormat(i18n.language, { weekday: 'short', timeZone: 'UTC' });
  const long = new Intl.DateTimeFormat(i18n.language, { weekday: 'long', day: 'numeric', month: 'long', timeZone: 'UTC' });
  const range = new Intl.DateTimeFormat(i18n.language, { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' });
  return {
    weekday: (date: string) => weekday.format(day(date)),
    dayOfMonth: (date: string) => day(date).getUTCDate(),
    long: (date: string) => long.format(day(date)),
    range: (from: string, to: string) => range.formatRange(day(from), day(to)),
  };
}
