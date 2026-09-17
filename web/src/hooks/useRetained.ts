import { useState } from 'react';

interface Retained<T> {
  /** The current value, or the last one while it animates away. */
  item: T | null;
  /** Whether the value is currently set. */
  open: boolean;
  /** Changes each time a value is opened, so forms remount fresh even when the same item reopens. */
  key: number;
}

/**
 * Keeps showing the last value after it is cleared, so a sheet or alert can play its exit animation with its
 * content intact instead of emptying the moment it starts closing.
 */
export function useRetained<T>(value: T | null): Retained<T> {
  const [state, setState] = useState<{ item: T | null; key: number; closed: boolean }>({ item: value, key: 0, closed: value === null });

  // Adjusting state while rendering (rather than in an effect) avoids a frame with stale content.
  if (value !== null && (state.closed || value !== state.item)) {
    setState({ item: value, key: state.key + 1, closed: false });
  } else if (value === null && !state.closed) {
    setState({ ...state, closed: true });
  }

  return { item: value ?? state.item, open: value !== null, key: state.key };
}
