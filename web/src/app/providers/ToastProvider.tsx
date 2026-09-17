import { AnimatePresence, motion } from 'motion/react';
import { AlertCircle, CheckCircle2, Info } from 'lucide-react';
import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';

export type ToastTone = 'success' | 'error' | 'info';

interface Toast {
  id: number;
  message: string;
  tone: ToastTone;
}

type Show = (message: string, tone?: ToastTone) => void;

const ToastContext = createContext<Show>(() => undefined);

export function useToast() {
  return useContext(ToastContext);
}

/*
  Code outside React (the query client's global error handlers) raises toasts through this channel. The mounted
  provider listens; with none mounted the message is dropped.
*/
const listeners = new Set<Show>();

export function announce(message: string, tone: ToastTone = 'info') {
  listeners.forEach((listener) => listener(message, tone));
}

const ICONS = {
  success: <CheckCircle2 size={18} className="shrink-0 text-positive" aria-hidden="true" />,
  error: <AlertCircle size={18} className="shrink-0 text-critical" aria-hidden="true" />,
  info: <Info size={18} className="shrink-0 text-accent" aria-hidden="true" />,
};

/**
 * Transient banners that drop in at the top centre, like iOS notifications. They float above the page, so they
 * never push content, and stay clear of the progress panel in the bottom corner. `banner` is a persistent item
 * (the offline capsule) that sits above them in the same stack, so the two never overlap.
 * A message already on screen isn't shown twice.
 */
export function ToastProvider({ children, banner }: { children: ReactNode; banner?: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const nextId = useRef(1);

  const show = useCallback<Show>((message, tone = 'info') => {
    const id = nextId.current++;
    setToasts((current) => (current.some((t) => t.message === message && t.tone === tone) ? current : [...current.slice(-2), { id, message, tone }]));
    window.setTimeout(() => setToasts((current) => current.filter((t) => t.id !== id)), tone === 'error' ? 6000 : 4200);
  }, []);

  useEffect(() => {
    listeners.add(show);
    return () => void listeners.delete(show);
  }, [show]);

  return (
    <ToastContext.Provider value={show}>
      {children}
      <div aria-live="polite" className="pointer-events-none fixed inset-x-0 top-[max(0.75rem,env(safe-area-inset-top))] z-[60] flex flex-col items-center gap-2 px-4">
        {banner}
        <AnimatePresence initial={false}>
          {toasts.map((toast) => (
            <motion.div
              key={toast.id}
              layout
              initial={{ opacity: 0, y: -16, scale: 0.96 }}
              animate={{ opacity: 1, y: 0, scale: 1 }}
              exit={{ opacity: 0, y: -12, scale: 0.96, transition: { duration: 0.18, ease: 'easeIn' } }}
              transition={{ type: 'spring', stiffness: 420, damping: 34 }}
              // A capsule on one line; a longer message wraps into a rounded rectangle, like an iOS notification.
              className="glass pointer-events-auto flex max-w-md items-center gap-2.5 rounded-[22px] px-4 py-2.5 text-[0.9375rem] leading-snug"
              // The container is the polite live region; errors also interrupt, as alerts.
              role={toast.tone === 'error' ? 'alert' : undefined}
            >
              {ICONS[toast.tone]}
              <span>{toast.message}</span>
            </motion.div>
          ))}
        </AnimatePresence>
      </div>
    </ToastContext.Provider>
  );
}
