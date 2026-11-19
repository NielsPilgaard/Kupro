import { FileArchive, FileSpreadsheet, PackageOpen, type LucideIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { ImportResponse } from '../../api/gen/types.gen';
import azureDevOps from '../../assets/marks/azure-devops.svg';
import github from '../../assets/marks/github.svg';
import jira from '../../assets/marks/jira.svg';
import linear from '../../assets/marks/linear.svg';
import monday from '../../assets/marks/monday.svg';
import trello from '../../assets/marks/trello.svg';
import youtrack from '../../assets/marks/youtrack.svg';

/**
 * Where an import can read from, in the order the source step shows them. `file` sources upload; the rest connect
 * with a token. `wip` marks a source shown but not available yet (design guide: source cards).
 */
export const sourceKinds = ['azure-devops', 'jira', 'csv', 'snapshot', 'kupro', 'linear', 'trello', 'github', 'monday', 'youtrack'] as const;
export type SourceKind = (typeof sourceKinds)[number];
/** Sources that connect with a token, those that also take an address, and those that upload a file. */
export type ConnectKind = Exclude<SourceKind, FileKind>;
export type UrlKind = 'azure-devops' | 'jira' | 'github' | 'youtrack';
export type FileKind = 'csv' | 'snapshot' | 'kupro';

export interface SourceInfo {
  kind: SourceKind;
  mark: string | LucideIcon;
  file?: string;
  url?: 'required' | 'optional';
  email?: boolean;
  local?: boolean;
  /** The token comes from the source's authorize page for Kupro's own key (Trello). */
  authorize?: boolean;
  wip?: boolean;
}

export const sources: SourceInfo[] = [
  { kind: 'azure-devops', mark: azureDevOps, url: 'required', local: true },
  { kind: 'jira', mark: jira, url: 'required', email: true, local: true },
  { kind: 'csv', mark: FileSpreadsheet, file: '.csv,text/csv' },
  { kind: 'snapshot', mark: FileArchive, file: '.zip,application/zip' },
  { kind: 'kupro', mark: PackageOpen, file: '.zip,application/zip' },
  { kind: 'linear', mark: linear },
  { kind: 'trello', mark: trello, authorize: true },
  { kind: 'github', mark: github, url: 'optional' },
  { kind: 'monday', mark: monday },
  { kind: 'youtrack', mark: youtrack, url: 'required' },
];

export const sourceOf = (kind: string) => sources.find((s) => s.kind === kind);

/** "Azure DevOps", or the kind itself for a source this build doesn't list. */
export function useSourceName() {
  const { t } = useTranslation();
  return (kind: string) => (sourceOf(kind) ? t(`importSource.${kind as SourceKind}.name`) : kind);
}

/** An import's name in lists: the source, and its address when it has one. */
export function useImportName() {
  const sourceName = useSourceName();
  return (i: Pick<ImportResponse, 'sourceKind' | 'baseUrl'>) => {
    const host = i.baseUrl?.replace(/^https?:\/\//, '');
    return host ? `${sourceName(i.sourceKind)} · ${host}` : sourceName(i.sourceKind);
  };
}

export const control = 'h-[var(--control)] min-w-0 rounded-r1 border border-line bg-panel px-2';
