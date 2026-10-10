import { useTranslation } from 'react-i18next';

const sizes = {
  sm: { mark: 22, text: 'text-lg' },
  lg: { mark: 32, text: 'text-2xl' },
} as const;

/** Stream mark + lowercase wordmark (docs/branding.md). */
export function Wordmark({ size = 'lg' }: { size?: keyof typeof sizes }) {
  const { t } = useTranslation();
  const { mark, text } = sizes[size];
  return (
    <div className="flex items-center gap-2" aria-label={t('app.name')} role="img">
      <svg width={mark} height={mark} viewBox="0 0 32 32" aria-hidden="true">
        <rect x="3" y="6.5" width="17" height="5" rx="2.5" className="fill-ink" />
        <rect x="9" y="13.5" width="20" height="5" rx="2.5" className="fill-acc" />
        <rect x="5" y="20.5" width="12" height="5" rx="2.5" className="fill-ink" opacity={0.5} />
      </svg>
      <span className={`font-display font-bold lowercase ${text}`} aria-hidden="true">
        {t('app.name')}
      </span>
    </div>
  );
}
