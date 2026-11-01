import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';

/** Sentinel at the end of a paged list: fetches the next page as it scrolls near, gone once everything is loaded. */
export function LoadMore({
  hasNextPage,
  isFetchingNextPage,
  fetchNextPage,
}: {
  hasNextPage: boolean;
  isFetchingNextPage: boolean;
  fetchNextPage(): unknown;
}) {
  const { t } = useTranslation();
  const ref = useRef<HTMLDivElement>(null);
  // Re-observing after each fetch fires again while the sentinel is still in view, so a tall screen fills up.
  useEffect(() => {
    if (!hasNextPage || isFetchingNextPage || !ref.current || typeof IntersectionObserver === 'undefined') return;
    const observer = new IntersectionObserver((entries) => entries.some((e) => e.isIntersecting) && fetchNextPage(), {
      rootMargin: '400px',
    });
    observer.observe(ref.current);
    return () => observer.disconnect();
  }, [hasNextPage, isFetchingNextPage, fetchNextPage]);
  if (!hasNextPage) return null;
  return (
    <div ref={ref} className="p-2 text-center text-sm text-mute">
      {t('common.loading')}
    </div>
  );
}
