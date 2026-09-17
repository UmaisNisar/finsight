import { createContext, useCallback, useContext, useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { cn } from '@/lib/cn';

export interface ConfirmOptions {
  title: string;
  message?: string;
  confirmLabel: string;
  cancelLabel?: string;
  destructive?: boolean;
}

type Confirm = (options: ConfirmOptions) => Promise<boolean>;

const ConfirmContext = createContext<Confirm | null>(null);

export function useConfirm(): Confirm {
  const confirm = useContext(ConfirmContext);
  if (!confirm) throw new Error('useConfirm must be used inside ConfirmProvider');
  return confirm;
}

/**
 * Apple-style alerts in place of window.confirm: a centred glass panel with a bold title, short message and
 * capsule buttons. Built on a modal <dialog>, so it stacks above open sheets, traps focus and closes on Escape.
 * Cancel receives initial focus, so a stray Enter never confirms a destructive action. The last alert stays
 * rendered while it animates out.
 */
export function ConfirmProvider({ children }: { children: ReactNode }) {
  const [options, setOptions] = useState<ConfirmOptions | null>(null);
  const [open, setOpen] = useState(false);
  const resolver = useRef<((confirmed: boolean) => void) | null>(null);
  const dialogRef = useRef<HTMLDialogElement>(null);
  const cancelRef = useRef<HTMLButtonElement>(null);
  const titleId = useId();
  const messageId = useId();

  const confirm = useCallback<Confirm>(
    (next) =>
      new Promise((resolve) => {
        // A new alert replaces one still waiting; the earlier caller is told it was cancelled.
        resolver.current?.(false);
        resolver.current = resolve;
        setOptions(next);
        setOpen(true);
      }),
    [],
  );

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog) return;
    if (open && !dialog.open) {
      dialog.showModal();
      cancelRef.current?.focus();
    } else if (!open && dialog.open) {
      dialog.close();
    }
  }, [open, options]);

  function settle(confirmed: boolean) {
    resolver.current?.(confirmed);
    resolver.current = null;
    setOpen(false);
  }

  return (
    <ConfirmContext.Provider value={confirm}>
      {children}
      <dialog
        ref={dialogRef}
        role="alertdialog"
        aria-labelledby={titleId}
        aria-describedby={options?.message ? messageId : undefined}
        onCancel={(event) => {
          event.preventDefault();
          settle(false);
        }}
        className="alert-dialog m-auto max-h-none max-w-none overflow-visible bg-transparent p-0 text-label backdrop:bg-black/25 backdrop:backdrop-blur-[2px]"
      >
        {options && (
          <div className="alert-panel glass w-[min(340px,calc(100vw-48px))] rounded-[30px] p-6 text-center">
            <h2 id={titleId} className="text-[1.0625rem] leading-snug font-semibold tracking-[-0.01em]">
              {options.title}
            </h2>
            {options.message && (
              <p id={messageId} className="mt-1.5 text-[0.875rem] leading-snug text-label-secondary">
                {options.message}
              </p>
            )}
            <div className="mt-5 grid grid-cols-2 gap-2.5">
              <button
                ref={cancelRef}
                type="button"
                onClick={() => settle(false)}
                className="button glass-control h-11 rounded-full text-[0.9375rem] font-medium"
              >
                {options.cancelLabel ?? 'Cancel'}
              </button>
              <button
                type="button"
                onClick={() => settle(true)}
                className={cn(
                  'button h-11 rounded-full text-[0.9375rem] font-semibold',
                  // Destructive actions use red text on glass, as iOS alerts do; it keeps contrast in both themes.
                  options.destructive ? 'glass-control text-critical' : 'glass-tint text-accent-contrast',
                )}
              >
                {options.confirmLabel}
              </button>
            </div>
          </div>
        )}
      </dialog>
    </ConfirmContext.Provider>
  );
}
