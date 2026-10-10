import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { getStatusOptions } from '../api/gen/@tanstack/react-query.gen';

/** Proves the SPA → generated client → API path works (task 001). */
export function ApiStatus() {
  const { t } = useTranslation();
  const { data, isPending, isError } = useQuery({ ...getStatusOptions(), retry: false });

  if (isPending) return <p className="text-sm text-mute">{t('status.checking')}</p>;
  if (isError) return <p className="text-sm text-bad">{t('status.offline')}</p>;

  return (
    <p className="flex items-center gap-2 text-sm text-mute">
      <span className="inline-block size-2 rounded-full bg-ok" aria-hidden="true" />
      {t('status.online', { version: data.version })}
    </p>
  );
}
