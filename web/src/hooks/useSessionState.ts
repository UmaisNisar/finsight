import { useCallback, useState } from 'react';

function read<T>(key: string, fallback: T): T {
  try {
    const raw = window.sessionStorage.getItem(key);
    return raw === null ? fallback : (JSON.parse(raw) as T);
  } catch {
    return fallback;
  }
}

/**
 * State remembered for this browser tab, so purely visual choices survive a refresh or an OAuth round trip.
 * Storage can be unavailable (private modes, blocked cookies); the state then simply lives in memory.
 * Never use it for anything the server should know.
 */
export function useSessionState<T>(key: string, fallback: T): [T, (value: T) => void] {
  const [value, setValue] = useState<T>(() => read(key, fallback));
  const set = useCallback(
    (next: T) => {
      setValue(next);
      try {
        if (next === null || next === undefined) window.sessionStorage.removeItem(key);
        else window.sessionStorage.setItem(key, JSON.stringify(next));
      } catch {
        // Not persisted; the in-memory value still applies.
      }
    },
    [key],
  );
  return [value, set];
}
