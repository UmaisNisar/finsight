import { X } from 'lucide-react';
import { useEffect, useId, useRef, type ReactNode } from 'react';
import { cn } from '@/lib/cn';
import { IconButton } from './Button';
import { Tooltip } from './Tooltip';

interface SheetProps {
  open: boolean;
  onClose: () => void;
  title: ReactNode;
  subtitle?: ReactNode;
  children: ReactNode;
  footer?: ReactNode;
  size?: 'md' | 'lg';
}

/**
 * A modal sheet built on the native <dialog> element, which provides focus trapping, Escape to close
 * and inert background for free. Slides in from the right on wide screens and up from the bottom on phones.
 * The panel stays rendered while closed, so it animates out with its content (see `.sheet` in styles.css);
 * callers keep showing the last item while `open` is false, typically with `useRetained`.
 */
export function Sheet({ open, onClose, title, subtitle, children, footer, size = 'md' }: SheetProps) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();

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
      aria-labelledby={titleId}
      onCancel={(event) => {
        // Let React own the open state, so the close animation runs from the same path as the Close button.
        event.preventDefault();
        onClose();
      }}
      onClick={(event) => {
        if (event.target === ref.current) onClose();
      }}
      className={cn(
        'sheet m-0 max-h-none max-w-none overflow-visible bg-transparent p-0 text-label backdrop:bg-black/20 backdrop:backdrop-blur-[3px]',
        'fixed inset-x-0 top-auto bottom-0 h-[92dvh] w-full',
        // overflow-visible: the panel's float shadow extends past the dialog box; the UA's overflow:auto clipped it into a hard band.
        'md:inset-y-0 md:right-0 md:left-auto md:h-dvh md:p-3',
        size === 'lg' ? 'md:w-[min(640px,100vw)]' : 'md:w-[min(480px,100vw)]',
      )}
    >
      <div className="sheet-panel glass flex h-full flex-col overflow-hidden rounded-t-[28px] md:rounded-[28px]">
        <header className="flex items-start gap-3 px-5 pt-5 pb-3 md:px-6">
          <div className="min-w-0 flex-1">
            {typeof title === 'string' ? (
              <Tooltip content={title} onlyWhenTruncated>
                <h2 id={titleId} className="title-section truncate">
                  {title}
                </h2>
              </Tooltip>
            ) : (
              <h2 id={titleId} className="title-section truncate">
                {title}
              </h2>
            )}
            {subtitle && <div className="caption mt-0.5 truncate">{subtitle}</div>}
          </div>
          <IconButton label="Close" onClick={onClose} className="glass-control -mt-1 -mr-2">
            <X size={18} aria-hidden="true" />
          </IconButton>
        </header>
        <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain px-5 pb-6 md:px-6">{children}</div>
        {footer && <footer className="flex min-h-[65px] flex-wrap items-center gap-2 border-t border-separator px-5 py-3 md:px-6">{footer}</footer>}
      </div>
    </dialog>
  );
}
