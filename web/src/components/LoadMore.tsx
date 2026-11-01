import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from './ui';

/**
 * Sentinel at the end of a paged list: fetches the next page as it scrolls near, gone once everything is loaded. A
 * failed fetch stops the paging and shows a retry instead, so a failing server is not asked again on every render.
 */
export function LoadMore({
  hasNextPage,
  isFetchingNextPage,
  isError,
  isFetchNextPageError,
  fetchNextPage,
  refetch,
}: {
  hasNextPage: boolean;
  isFetchingNextPage: boolean;
  isError: boolean;
  isFetchNextPageError: boolean;
  fetchNextPage(): unknown;
  refetch(): unknown;
}) {
  const { t } = useTranslation();
  const ref = useRef<HTMLDivElement>(null);
  // Re-observing after each fetch fires again while the sentinel is still in view, so a tall screen fills up.
  useEffect(() => {
    if (!hasNextPage || isFetchingNextPage || isError || !ref.current || typeof IntersectionObserver === 'undefined') return;
    const observer = new IntersectionObserver((entries) => entries.some((e) => e.isIntersecting) && fetchNextPage(), {
      rootMargin: '400px',
    });
    observer.observe(ref.current);
    return () => observer.disconnect();
  }, [hasNextPage, isFetchingNextPage, isError, fetchNextPage]);
  if (isError) {
    return (
      <div className="flex flex-col items-center gap-2 p-2 text-center text-sm">
        <span className="text-bad">{t('errors.loadFailed')}</span>
        <Button onClick={() => (isFetchNextPageError ? fetchNextPage() : refetch())}>{t('common.retry')}</Button>
      </div>
    );
  }
  if (!hasNextPage) return null;
  return (
    <div ref={ref} className="p-2 text-center text-sm text-mute">
      {t('common.loading')}
    </div>
  );
}
