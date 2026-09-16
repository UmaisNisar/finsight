import { X } from 'lucide-react';
import { useEffect, useRef, type ReactNode } from 'react';
import { cn } from '@/lib/cn';
import { IconButton } from './Button';

interface SheetProps {
  open: boolean;
  onClose: () => void;
  title: string;
  subtitle?: ReactNode;
  children: ReactNode;
  footer?: ReactNode;
  size?: 'md' | 'lg';
}

/**
 * A modal sheet built on the native <dialog> element, which provides focus trapping, Escape to close
 * and inert background for free. Slides in from the right on wide screens and up from the bottom on phones.
 */
export function Sheet({ open, onClose, title, subtitle, children, footer, size = 'md' }: SheetProps) {
  const ref = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) {
      dialog.showModal();
    } else if (!open && dialog.open) {
      dialog.close();
    }
  }, [open]);

  return (
    // Clicking the backdrop closes the sheet; keyboard users close it with Escape, which <dialog> handles natively.
    // eslint-disable-next-line jsx-a11y/click-events-have-key-events, jsx-a11y/no-noninteractive-element-interactions
    <dialog
      ref={ref}
      aria-labelledby="sheet-title"
      onClose={onClose}
      onCancel={(event) => {
        event.preventDefault();
        onClose();
      }}
      onClick={(event) => {
        if (event.target === ref.current) onClose();
      }}
      className={cn(
        'sheet m-0 max-h-none max-w-none bg-transparent p-0 text-label backdrop:bg-black/25 backdrop:backdrop-blur-[2px]',
        'fixed inset-x-0 bottom-0 top-auto h-[92dvh] w-full',
        'md:inset-y-0 md:right-0 md:left-auto md:h-dvh md:p-3',
        size === 'lg' ? 'md:w-[min(640px,100vw)]' : 'md:w-[min(480px,100vw)]',
      )}
    >
      {open && (
        <div className="flex h-full flex-col overflow-hidden rounded-t-[22px] bg-canvas shadow-float md:rounded-[22px]">
          <header className="flex items-start gap-3 px-5 pt-5 pb-3 md:px-6">
            <div className="min-w-0 flex-1">
              <h2 id="sheet-title" className="title-section truncate">
                {title}
              </h2>
              {subtitle && <div className="caption mt-0.5">{subtitle}</div>}
            </div>
            <IconButton label="Close" onClick={onClose} className="-mt-1 -mr-2 bg-fill">
              <X size={18} />
            </IconButton>
          </header>
          <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain px-5 pb-6 md:px-6">{children}</div>
          {footer && <footer className="flex flex-wrap items-center gap-2 border-t border-separator px-5 py-3 md:px-6">{footer}</footer>}
        </div>
      )}
    </dialog>
  );
}
