import { useCallback, useRef } from 'react';

/**
 * Runs an async action at most once at a time. Extra calls while it is in flight are ignored synchronously, so
 * a fast double click (two events before React re-renders the disabled button) can't start the action twice.
 * Resolves to undefined for ignored calls and for actions that fail; failures surface through mutation state.
 */
export function useSingleFlight() {
  const busy = useRef(false);
  return useCallback(async <T>(action: () => Promise<T>): Promise<T | undefined> => {
    if (busy.current) return undefined;
    busy.current = true;
    try {
      return await action();
    } catch {
      return undefined;
    } finally {
      busy.current = false;
    }
  }, []);
}
