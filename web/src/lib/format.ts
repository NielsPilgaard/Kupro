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
