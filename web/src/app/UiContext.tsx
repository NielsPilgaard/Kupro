import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';

/** Palette pages double as pickers, so S/A/P/L and the palette share one keyboard-first UI. */
export type PalettePage = 'root' | 'state' | 'assign' | 'priority' | 'label';

/** What the Log time dialog opens for: an issue (or a pick), and a close to save with it (ADR-021 Locking). */
export interface LogTimeRequest {
  issue?: string;
  date?: string;
  /** Done or Canceled: the entry and the state change save together. */
  state?: 'done' | 'canceled';
  /** "Mark Done without time": the move the dialog interrupted. */
  onSkip?(): void;
  /** After the entry and the close are saved: a board drop applies its position here. */
  onSaved?(): void;
}

interface UiState {
  /** Issue the keyboard acts on: the open panel's issue, else the highlighted row. */
  activeKey: string | null;
  setActiveKey(key: string | null): void;
  palette: PalettePage | null;
  openPalette(page?: PalettePage): void;
  closePalette(): void;
  createOpen: boolean;
  setCreateOpen(open: boolean): void;
  sidebarCollapsed: boolean;
  toggleSidebar(): void;
  /** A view can take over `C` (the plan creates in the selected month); null restores the issue dialog. */
  createOverride: (() => void) | null;
  setCreateOverride(fn: (() => void) | null): void;
  /** The open issue panel takes over S/A/P/L with its inline pickers; returns false to fall back to the palette. */
  pickerOverride: ((page: PalettePage) => boolean) | null;
  setPickerOverride(fn: ((page: PalettePage) => boolean) | null): void;
  logTime: LogTimeRequest | null;
  openLogTime(request?: LogTimeRequest): void;
  closeLogTime(): void;
}

const UiContext = createContext<UiState | null>(null);

// eslint-disable-next-line react-refresh/only-export-components
export function useUi() {
  const ui = useContext(UiContext);
  if (!ui) throw new Error('useUi outside UiProvider');
  return ui;
}

export function UiProvider({ children }: { children: ReactNode }) {
  const [activeKey, setActiveKey] = useState<string | null>(null);
  const [palette, setPalette] = useState<PalettePage | null>(null);
  const [createOpen, setCreateOpen] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);
  const [createOverride, setOverride] = useState<(() => void) | null>(null);
  // Stable, so views can set it from an effect without re-running it. Functions in state must be wrapped.
  const setCreateOverride = useCallback((fn: (() => void) | null) => setOverride(() => fn), []);
  const [pickerOverride, setPicker] = useState<((page: PalettePage) => boolean) | null>(null);
  const setPickerOverride = useCallback((fn: ((page: PalettePage) => boolean) | null) => setPicker(() => fn), []);
  const [logTime, setLogTime] = useState<LogTimeRequest | null>(null);
  // Stable, so the shell can close the dialog when the workspace changes without closing it on every open.
  const closeLogTime = useCallback(() => setLogTime(null), []);

  const value = useMemo<UiState>(
    () => ({
      activeKey,
      setActiveKey,
      palette,
      openPalette: (page = 'root') => setPalette(page),
      closePalette: () => setPalette(null),
      createOpen,
      setCreateOpen,
      sidebarCollapsed,
      toggleSidebar: () => setSidebarCollapsed((c) => !c),
      createOverride,
      setCreateOverride,
      pickerOverride,
      setPickerOverride,
      logTime,
      openLogTime: (request = {}) => setLogTime(request),
      closeLogTime,
    }),
    [activeKey, palette, createOpen, sidebarCollapsed, createOverride, setCreateOverride, pickerOverride, setPickerOverride, logTime, closeLogTime],
  );

  return <UiContext.Provider value={value}>{children}</UiContext.Provider>;
}
