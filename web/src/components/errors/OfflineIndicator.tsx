import { useQueryClient } from '@tanstack/react-query';
import { WifiOff } from 'lucide-react';
import { AnimatePresence, motion } from 'motion/react';
import { useEffect, useRef, useSyncExternalStore } from 'react';

function subscribe(onChange: () => void) {
  window.addEventListener('online', onChange);
  window.addEventListener('offline', onChange);
  return () => {
    window.removeEventListener('online', onChange);
    window.removeEventListener('offline', onChange);
  };
}

export function useOnline(): boolean {
  return useSyncExternalStore(
    subscribe,
    () => navigator.onLine,
    () => true,
  );
}

/**
 * A small glass capsule at the top of the screen while the device is offline. It drops in and lifts away like a
 * toast, and when the connection returns every query on screen is refetched.
 */
export function OfflineIndicator() {
  const online = useOnline();
  const client = useQueryClient();
  const wasOffline = useRef(!online);

  useEffect(() => {
    if (!online) {
      wasOffline.current = true;
    } else if (wasOffline.current) {
      wasOffline.current = false;
      // cancelRefetch: false joins requests TanStack Query already resumed instead of restarting them.
      void client.refetchQueries({ type: 'active' }, { cancelRefetch: false });
    }
  }, [online, client]);

  return (
    <AnimatePresence initial={false}>
      {!online && (
        <motion.div
          key="offline"
          layout
          role="status"
          initial={{ opacity: 0, y: -16, scale: 0.96 }}
          animate={{ opacity: 1, y: 0, scale: 1 }}
          exit={{ opacity: 0, y: -12, scale: 0.96, transition: { duration: 0.18, ease: 'easeIn' } }}
          transition={{ type: 'spring', stiffness: 420, damping: 34 }}
          className="glass pointer-events-auto flex items-center gap-2 rounded-full py-2 pr-4 pl-3.5 text-[0.875rem] font-medium"
        >
          <WifiOff size={16} className="shrink-0 text-label-secondary" aria-hidden="true" />
          You’re offline
        </motion.div>
      )}
    </AnimatePresence>
  );
}
