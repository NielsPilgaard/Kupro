import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';

export function NotFound() {
  const { t } = useTranslation();
  return (
    <main className="flex h-full flex-col items-center justify-center gap-4">
      <h1 className="font-display text-2xl font-bold">{t('notFound.title')}</h1>
      <Link to="/" className="text-acc-ink underline">
        {t('notFound.back')}
      </Link>
    </main>
  );
}
