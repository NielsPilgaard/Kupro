import { useQuery } from '@tanstack/react-query';
import { Outlet } from '@tanstack/react-router';
import { LogOut } from 'lucide-react';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { getMeOptions } from '../api/gen/@tanstack/react-query.gen';
import { signOut } from '../auth/auth';
import { Wordmark } from '../components/Wordmark';

/** Signed-in frame. Task 007 replaces this with the full app shell (sidebar, topbar, palette). */
export function AuthedLayout() {
  const { t, i18n } = useTranslation();
  const { data: me } = useQuery(getMeOptions());

  // The profile's language wins over the browser's (ADR-011).
  useEffect(() => {
    if (me && me.locale !== i18n.language) void i18n.changeLanguage(me.locale);
  }, [me, i18n]);

  return (
    <div className="flex h-full flex-col">
      <header className="flex h-[var(--topbar)] shrink-0 items-center justify-between border-b border-line px-4">
        <Wordmark size="sm" />
        {me && (
          <div className="flex items-center gap-3 text-mute">
            <span className="truncate">{me.displayName}</span>
            <button
              type="button"
              onClick={() => void signOut()}
              className="flex h-[var(--control)] items-center gap-2 rounded-r1 px-2 hover:bg-sel hover:text-ink"
            >
              <LogOut size={16} strokeWidth={1.5} aria-hidden="true" />
              {t('auth.signOut')}
            </button>
          </div>
        )}
      </header>
      <div className="min-h-0 flex-1 overflow-auto">
        <Outlet />
      </div>
    </div>
  );
}
