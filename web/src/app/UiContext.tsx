import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';

/** Palette pages double as pickers, so S/A/P/L and the palette share one keyboard-first UI. */
export type PalettePage = 'root' | 'state' | 'assign' | 'priority' | 'label';

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
    }),
    [activeKey, palette, createOpen, sidebarCollapsed, createOverride, setCreateOverride],
  );

  return <UiContext.Provider value={value}>{children}</UiContext.Provider>;
}
