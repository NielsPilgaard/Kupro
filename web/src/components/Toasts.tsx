import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

/** A button in the toast, such as Undo; pressing it dismisses the toast. */
export interface ToastAction {
  label: string;
  run(): void;
}

interface Toast {
  id: number;
  message: string;
  tone: 'info' | 'error';
  action?: ToastAction;
}

interface ToastApi {
  info(message: string, action?: ToastAction): void;
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
    (message: string, tone: Toast['tone'], action?: ToastAction) => {
      const id = Date.now() + Math.random();
      setToasts((all) => [...all.slice(-3), { id, message, tone, action }]);
      // A toast with an action stays a little longer, so there is time to reach Undo.
      if (tone === 'info') setTimeout(() => dismiss(id), action ? 8000 : 5000);
    },
    [dismiss],
  );
  const api = useMemo<ToastApi>(() => ({ info: (m, action) => push(m, 'info', action), error: (m) => push(m, 'error') }), [push]);

  return (
    <ToastContext.Provider value={api}>
      {children}
      <div className="pointer-events-none fixed right-4 bottom-[var(--toast-bottom,1rem)] z-50 flex flex-col items-end gap-2" role="status" aria-live="polite">
        {toasts.map((toast) => (
          <div
            key={toast.id}
            className="pointer-events-auto flex max-w-sm items-start gap-3 rounded-r2 bg-ink px-3 py-2 text-sm text-bg shadow-float"
          >
            {toast.tone === 'error' && <span className="font-semibold text-bad">!</span>}
            <span className="min-w-0 flex-1">{toast.message}</span>
            {toast.action && (
              <button
                type="button"
                onClick={() => {
                  dismiss(toast.id);
                  toast.action!.run();
                }}
                className="font-semibold text-acc hover:underline"
              >
                {toast.action.label}
              </button>
            )}
            <button type="button" onClick={() => dismiss(toast.id)} className="opacity-70 hover:opacity-100" aria-label={t('common.dismiss')}>
              ×
            </button>
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}
