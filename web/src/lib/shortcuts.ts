import { useEffect, useRef } from 'react';

type Handler = (event: KeyboardEvent) => void;

/** True while the user types, so single-key shortcuts don't fire in fields. */
function isTyping(target: EventTarget | null) {
  const el = target as HTMLElement | null;
  return !!el && (el.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(el.tagName));
}

/**
 * Global keyboard shortcuts. Keys: a lowercase key (`c`, `[`, `escape`, `arrowdown`) or `mod+key` for Ctrl/Cmd.
 * Single keys are ignored while typing; `mod+` combos and `escape` always work.
 */
export function useShortcuts(map: Record<string, Handler | false | undefined>) {
  const ref = useRef(map);
  useEffect(() => {
    ref.current = map;
  });

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      const key = event.key.toLowerCase();
      const mod = event.ctrlKey || event.metaKey;
      const combo = mod ? `mod+${key}` : key;
      const handler = ref.current[combo];
      if (!handler) return;
      if (!mod && key !== 'escape' && (isTyping(event.target) || event.altKey)) return;
      if (event.defaultPrevented) return;
      event.preventDefault();
      handler(event);
    }

    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);
}
