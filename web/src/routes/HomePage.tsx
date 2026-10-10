import { useTranslation } from 'react-i18next';
import { ApiStatus } from '../components/ApiStatus';
import { Wordmark } from '../components/Wordmark';

export function HomePage() {
  const { t } = useTranslation();
  return (
    <main className="flex h-full flex-col items-center justify-center gap-6 p-4">
      <Wordmark />
      <p className="text-lg text-mute">{t('app.tagline')}</p>
      <ApiStatus />
    </main>
  );
}
