import { useSyncExternalStore } from 'react';

/*
  Whether the current signed-out state came from the session ending mid-use (a 401), rather than a first visit or
  choosing Sign out. Set once when a signed-in session gets a 401, cleared when someone signs in again, and lost on
  reload, so the welcome screen explains it exactly once.
*/
let expired = false;
const listeners = new Set<() => void>();

function set(value: boolean) {
  if (expired === value) return;
  expired = value;
  listeners.forEach((listener) => listener());
}

export const sessionNotice = {
  markExpired: () => set(true),
  clear: () => set(false),
  isExpired: () => expired,
};

const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => void listeners.delete(listener);
};

export function useSessionExpired(): boolean {
  return useSyncExternalStore(subscribe, sessionNotice.isExpired, sessionNotice.isExpired);
}
