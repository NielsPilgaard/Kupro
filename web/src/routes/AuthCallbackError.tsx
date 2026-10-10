import { useTranslation } from 'react-i18next';
import { signIn } from '../auth/auth';

export function AuthCallbackError() {
  const { t } = useTranslation();
  return (
    <main className="flex h-full flex-col items-center justify-center gap-4">
      <p className="text-md">{t('auth.signInFailed')}</p>
      <button
        type="button"
        onClick={() => void signIn('/')}
        className="h-[var(--control)] rounded-r1 bg-acc px-3 font-semibold text-on-acc"
      >
        {t('auth.retry')}
      </button>
    </main>
  );
}
