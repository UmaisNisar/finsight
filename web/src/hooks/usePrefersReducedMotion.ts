import { useSyncExternalStore } from 'react';

const QUERY = '(prefers-reduced-motion: reduce)';

function subscribe(onChange: () => void) {
  const list = window.matchMedia?.(QUERY);
  list?.addEventListener?.('change', onChange);
  return () => list?.removeEventListener?.('change', onChange);
}

const read = () => window.matchMedia?.(QUERY).matches ?? false;

/** Whether the OS asks for reduced motion, live. Read directly (not cached per page), so tests can switch it. */
export function usePrefersReducedMotion(): boolean {
  return useSyncExternalStore(subscribe, read, () => false);
}
