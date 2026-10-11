import { useTranslation } from 'react-i18next';

/** Hours in the user's locale: "8h" / "4,5 t" (design guide: writing in the UI). */
export function useHours() {
  const { t, i18n } = useTranslation();
  const number = new Intl.NumberFormat(i18n.language, { maximumFractionDigits: 1 });
  return (hours: number) => t('units.hours', { value: number.format(hours) });
}

export function useDate() {
  const { i18n } = useTranslation();
  const format = new Intl.DateTimeFormat(i18n.language, { day: 'numeric', month: 'short' });
  return (iso: string | null | undefined) => (iso ? format.format(new Date(iso)) : '');
}

/** "3 min ago" / "for 3 min. siden"; older than a week shows the date. */
export function useRelativeTime() {
  const { i18n } = useTranslation();
  const relative = new Intl.RelativeTimeFormat(i18n.language, { numeric: 'auto', style: 'short' });
  const date = new Intl.DateTimeFormat(i18n.language, { day: 'numeric', month: 'short' });
  return (iso: string) => {
    const seconds = (new Date(iso).getTime() - Date.now()) / 1000;
    const abs = Math.abs(seconds);
    if (abs < 60) return relative.format(Math.round(seconds), 'second');
    if (abs < 3600) return relative.format(Math.round(seconds / 60), 'minute');
    if (abs < 86_400) return relative.format(Math.round(seconds / 3600), 'hour');
    if (abs < 7 * 86_400) return relative.format(Math.round(seconds / 86_400), 'day');
    return date.format(new Date(iso));
  };
}
