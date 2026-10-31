import { Command } from 'cmdk';
import { Check, Plus, Search } from 'lucide-react';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Kbd } from './ui';

export interface PickerOption {
  value: string;
  label: string;
  icon?: ReactNode;
  hint?: ReactNode;
  /** Group heading; groups show in the order their first option appears. */
  group?: string;
  /** Digit that picks this option while the search is empty (1–9, 0). */
  key?: string;
}

/** Filled chip that opens the picker (design: chip trigger, tinted while open). */
export const chip = 'h-7 bg-sunk px-2 hover:bg-sel aria-expanded:bg-sel';

/**
 * Anchored, searchable select: the trigger shows the value, the popover filters as you type, ↑↓ ↵ or a digit pick,
 * Esc or a click outside closes and hands focus back to the trigger. `multi` stays open so several values can be toggled.
 */
export function Picker({
  label,
  placeholder,
  options,
  selected,
  onPick,
  onCreate,
  multi = false,
  open: controlled,
  onOpenChange,
  shortcut,
  className = chip,
  children,
}: {
  label: string;
  placeholder: string;
  options: PickerOption[];
  selected: string[];
  onPick(value: string): void;
  /** Offers "add <typed>" when the text matches no option (labels). */
  onCreate?(text: string): void;
  multi?: boolean;
  open?: boolean;
  onOpenChange?(open: boolean): void;
  shortcut?: string;
  className?: string;
  children: ReactNode;
}) {
  const { t } = useTranslation();
  const [own, setOwn] = useState(false);
  const open = controlled ?? own;
  const [query, setQuery] = useState('');
  const root = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);

  const setOpen = (next: boolean) => {
    setOwn(next);
    onOpenChange?.(next);
    if (!next) {
      setQuery('');
      trigger.current?.focus();
    }
  };

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (!root.current?.contains(e.target as Node)) {
        setOwn(false);
        setQuery('');
        onOpenChange?.(false);
      }
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [open, onOpenChange]);

  const pick = (value: string) => {
    onPick(value);
    if (multi) setQuery('');
    else setOpen(false);
  };

  const groups = [...new Set(options.map((o) => o.group ?? ''))];
  const typed = query.trim().toLowerCase();
  const canCreate = !!onCreate && typed !== '' && !options.some((o) => o.label.toLowerCase() === typed);

  const item = (o: PickerOption) => (
    <Command.Item
      key={o.value}
      value={o.value}
      keywords={[o.label]}
      onSelect={() => pick(o.value)}
      className="flex h-8 cursor-pointer items-center gap-2 rounded-r1 px-2 data-[selected=true]:bg-sel"
    >
      {o.icon && <span className="flex w-5 shrink-0 justify-center">{o.icon}</span>}
      <span className="min-w-0 flex-1 truncate">
        <Highlight text={o.label} query={typed} />
      </span>
      {o.hint && <span className="shrink-0 text-sm text-mute">{o.hint}</span>}
      <Check size={14} strokeWidth={2} aria-hidden="true" className={`shrink-0 text-acc-ink ${selected.includes(o.value) ? '' : 'invisible'}`} />
      {o.key && <span className="w-3 shrink-0 text-right font-mono text-xs text-mute">{o.key}</span>}
    </Command.Item>
  );

  return (
    <div ref={root} className="relative max-w-full">
      <button
        ref={trigger}
        type="button"
        aria-haspopup="listbox"
        aria-expanded={open}
        onClick={() => setOpen(!open)}
        className={`group flex max-w-full items-center gap-1.5 rounded-r1 text-left transition-colors duration-[var(--dur-1)] ${className}`}
      >
        {children}
        {shortcut && (
          <span className="opacity-0 group-hover:opacity-100">
            <Kbd>{shortcut}</Kbd>
          </span>
        )}
      </button>
      {open && (
        <Command
          label={label}
          loop
          defaultValue={selected[0]}
          onKeyDown={(e) => {
            if (e.key === 'Escape') {
              e.preventDefault();
              e.stopPropagation();
              setOpen(false);
              return;
            }
            const byKey = query === '' && options.find((o) => o.key === e.key);
            if (byKey) {
              e.preventDefault();
              pick(byKey.value);
            }
          }}
          className="absolute top-full left-0 z-20 mt-1 flex max-h-96 w-72 flex-col overflow-hidden rounded-r2 border border-line bg-panel shadow-float"
        >
          <div className="flex h-9 shrink-0 items-center gap-2 border-b border-line px-2.5">
            <Search size={14} strokeWidth={1.5} aria-hidden="true" className="shrink-0 text-mute" />
            <Command.Input
              autoFocus
              value={query}
              onValueChange={setQuery}
              placeholder={placeholder}
              className="min-w-0 flex-1 bg-transparent outline-none focus-visible:outline-none"
            />
          </div>
          <Command.List className="min-h-0 overflow-auto p-1">
            {!canCreate && <Command.Empty className="p-2 text-mute">{t('palette.empty')}</Command.Empty>}
            {groups.map((g) =>
              g ? (
                <Command.Group
                  key={g}
                  heading={g}
                  className="[&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:pt-2 [&_[cmdk-group-heading]]:pb-1 [&_[cmdk-group-heading]]:text-xs [&_[cmdk-group-heading]]:font-semibold [&_[cmdk-group-heading]]:text-mute"
                >
                  {options.filter((o) => o.group === g).map(item)}
                </Command.Group>
              ) : (
                options.filter((o) => !o.group).map(item)
              ),
            )}
            {canCreate && (
              <Command.Item
                value={`create:${typed}`}
                keywords={[typed]}
                onSelect={() => {
                  onCreate(typed);
                  setQuery('');
                }}
                className="flex h-8 cursor-pointer items-center gap-2 rounded-r1 px-2 data-[selected=true]:bg-sel"
              >
                <span className="flex w-5 shrink-0 justify-center">
                  <Plus size={14} strokeWidth={1.5} aria-hidden="true" className="text-mute" />
                </span>
                <span className="truncate">{t('palette.newLabel', { name: typed })}</span>
              </Command.Item>
            )}
          </Command.List>
          <footer className="flex shrink-0 items-center gap-3 border-t border-line px-3 py-1.5 text-xs text-mute">
            <span>
              <Kbd>↑↓</Kbd> {t('picker.move')}
            </span>
            <span>
              <Kbd>↵</Kbd> {t('picker.choose')}
            </span>
            <span>
              <Kbd>Esc</Kbd> {t('common.close')}
            </span>
          </footer>
        </Command>
      )}
    </div>
  );
}

/** The typed text in the label, bold in the accent (design: combobox match). */
function Highlight({ text, query }: { text: string; query: string }) {
  const at = query ? text.toLowerCase().indexOf(query) : -1;
  if (at < 0) return text;
  return (
    <>
      {text.slice(0, at)}
      <mark className="bg-transparent font-semibold text-acc-ink">{text.slice(at, at + query.length)}</mark>
      {text.slice(at + query.length)}
    </>
  );
}
