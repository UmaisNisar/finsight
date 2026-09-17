import type { ReactNode } from 'react';

/**
 * Development-only switch for seeing error screens: add `?__crash=` to the URL.
 *   root   the full-page screen (outside the app shell)
 *   page   or `1`, the in-shell page error
 *   card   every card-level fallback, or a single one by its boundary name (e.g. `cash-flow`)
 *   chunk  the "FinSight was updated" screen for a page whose code failed to load
 * `import.meta.env.DEV` is replaced with `false` in production builds, so all of this is removed there.
 */
export function devCrash(target: string, search: string = typeof window === 'undefined' ? '' : window.location.search) {
  if (!import.meta.env.DEV) return;
  const value = new URLSearchParams(search).get('__crash');
  if (value === null) return;
  const wanted = value === '1' ? 'page' : value;
  if (wanted === 'chunk' && target === 'page') {
    throw new TypeError('Failed to fetch dynamically imported module: http://localhost:5173/src/features/__crash.tsx');
  }
  if (wanted === target || target === `card:${wanted}` || (wanted === 'card' && target.startsWith('card:'))) {
    throw new Error(`Test crash (?__crash=${value}) in ${target}`);
  }
}

/** Renders its children, first throwing if `?__crash=` targets it (development only). */
export function DevCrash({ target, search, children }: { target: string; search?: string; children?: ReactNode }) {
  devCrash(target, search);
  return children;
}
