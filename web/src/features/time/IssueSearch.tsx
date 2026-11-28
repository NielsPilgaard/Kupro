import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { listIssuesOptions } from '../../api/gen/@tanstack/react-query.gen';
import type { IssueSummary } from '../../api/gen/types.gen';
import { closed } from '../../api/time';
import { StatusRing } from '../../components/ui';

/** Pick an issue: open ones you touched lately, or a server search from two characters (the palette's search). */
export function IssueSearch({
  workspace,
  value,
  onPick,
  exclude = [],
  autoFocus = false,
}: {
  workspace: string;
  value?: string;
  onPick(issue: IssueSummary): void;
  /** Keys already on the timesheet. */
  exclude?: string[];
  autoFocus?: boolean;
}) {
  const { t } = useTranslation();
  const [query, setQuery] = useState('');
  const [search, setSearch] = useState('');
  useEffect(() => {
    const id = setTimeout(() => setSearch(query.trim()), 120);
    return () => clearTimeout(id);
  }, [query]);
  const searching = search.length >= 2;
  const { data: recent } = useQuery(
    listIssuesOptions({ path: { workspace }, query: { sort: 'updated', limit: 8, state: ['todo', 'inProgress', 'inReview', 'backlog'] } }),
  );
  const { data: found } = useQuery({
    ...listIssuesOptions({ path: { workspace }, query: { q: search, limit: 8 } }),
    enabled: searching,
    placeholderData: keepPreviousData,
  });
  const items = ((searching ? found?.items : recent?.items) ?? []).filter((i) => !exclude.includes(i.key));
  return (
    <div className="flex flex-col gap-1">
      <input
        autoFocus={autoFocus}
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        placeholder={t('time.log.searchIssue')}
        aria-label={t('time.log.searchIssue')}
        className="h-[var(--control)] rounded-r1 border border-line bg-panel px-2"
      />
      <ul role="listbox" aria-label={t('time.log.issue')} className="flex max-h-48 flex-col overflow-auto rounded-r1 border border-line">
        {items.map((i) => (
          <li key={i.key} role="option" aria-selected={value === i.key} aria-disabled={closed(i.state)}>
            <button
              type="button"
              disabled={closed(i.state)}
              onClick={() => onPick(i)}
              className={`flex h-8 w-full items-center gap-2 px-2 text-left hover:bg-sel disabled:opacity-50 ${value === i.key ? 'bg-sel font-semibold' : ''}`}
            >
              <StatusRing state={i.state} />
              <span className="font-mono text-sm text-mute">{i.key}</span>
              <span className="truncate">{i.title}</span>
              {closed(i.state) && <span className="ml-auto text-xs text-mute">{t('time.locked')}</span>}
            </button>
          </li>
        ))}
        {items.length === 0 && <li className="p-2 text-sm text-mute">{t('palette.empty')}</li>}
      </ul>
    </div>
  );
}
