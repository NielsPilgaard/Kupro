import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

interface Toast {
  id: number;
  message: string;
  tone: 'info' | 'error';
}

interface ToastApi {
  info(message: string): void;
  error(message: string): void;
}

const ToastContext = createContext<ToastApi>({ info: () => {}, error: () => {} });

// eslint-disable-next-line react-refresh/only-export-components
export const useToast = () => useContext(ToastContext);

/** Bottom-right, inverted. Info auto-dismisses after 5 s; errors stay until dismissed (design guide). */
export function ToastProvider({ children }: { children: ReactNode }) {
  const { t } = useTranslation();
  const [toasts, setToasts] = useState<Toast[]>([]);

  const dismiss = useCallback((id: number) => setToasts((all) => all.filter((x) => x.id !== id)), []);
  const push = useCallback(
    (message: string, tone: Toast['tone']) => {
      const id = Date.now() + Math.random();
      setToasts((all) => [...all.slice(-3), { id, message, tone }]);
      if (tone === 'info') setTimeout(() => dismiss(id), 5000);
    },
    [dismiss],
  );
  const api = useMemo<ToastApi>(() => ({ info: (m) => push(m, 'info'), error: (m) => push(m, 'error') }), [push]);

  return (
    <ToastContext.Provider value={api}>
      {children}
      <div className="pointer-events-none fixed right-4 bottom-4 z-50 flex flex-col items-end gap-2" role="status" aria-live="polite">
        {toasts.map((toast) => (
          <div
            key={toast.id}
            className="pointer-events-auto flex max-w-sm items-start gap-3 rounded-r2 bg-ink px-3 py-2 text-sm text-bg shadow-float"
          >
            {toast.tone === 'error' && <span className="font-semibold text-bad">!</span>}
            <span className="min-w-0 flex-1">{toast.message}</span>
            <button type="button" onClick={() => dismiss(toast.id)} className="opacity-70 hover:opacity-100" aria-label={t('common.dismiss')}>
              ×
            </button>
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}
