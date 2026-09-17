import { AnimatePresence, motion } from 'motion/react';
import { Check, X } from 'lucide-react';
import { useRef, type ReactNode } from 'react';
import type { UploadState } from '@/app/providers/JobsProvider';
import { cn } from '@/lib/cn';
import { IconButton } from './ui/Button';

export interface UploadRowData {
  key: string;
  name: string;
  state: UploadState;
  /** Why it failed. */
  message?: string | null;
  /** Extra detail once done, like "CIBC · 42 transactions". */
  detail?: string | null;
  onRemove?: () => void;
}

const STATE_TEXT: Record<UploadState, string> = {
  queued: 'Waiting',
  uploading: 'Uploading…',
  processing: 'Reading…',
  done: 'Done',
  failed: 'Failed',
};

const POP = { type: 'spring', stiffness: 520, damping: 26 } as const;

function StateIcon({ state }: { state: UploadState }) {
  switch (state) {
    case 'done':
      return (
        <motion.span initial={{ scale: 0.4, opacity: 0 }} animate={{ scale: 1, opacity: 1 }} transition={POP} className="flex size-[18px] items-center justify-center rounded-full bg-positive text-white">
          <Check size={11} strokeWidth={3.25} />
        </motion.span>
      );
    case 'failed':
      return (
        <motion.span initial={{ scale: 0.4, opacity: 0 }} animate={{ scale: 1, opacity: 1 }} transition={POP} className="flex size-[18px] items-center justify-center rounded-full bg-critical text-white">
          <X size={11} strokeWidth={3.25} />
        </motion.span>
      );
    case 'queued':
      return <span className="block size-[18px] rounded-full border-[1.5px] border-dashed border-label-tertiary/60" />;
    default:
      return <span className={cn('block size-[18px] animate-spin rounded-full border-2', state === 'uploading' ? 'border-label-tertiary/25 border-t-label-secondary' : 'border-accent/25 border-t-accent')} />;
  }
}

/**
 * A compact list of files being uploaded: name, a state glyph and a word for where each one is. Rows slide in as
 * files are added and fold away when cleared. Announced politely to screen readers as states change.
 */
export function UploadProgressList({ rows, label = 'Uploads', className }: { rows: UploadRowData[]; label?: string; className?: string }) {
  return (
    <ul aria-label={label} aria-live="polite" className={cn('rounded-[14px] bg-fill/55 px-3', className)}>
      <AnimatePresence initial={false}>
        {rows.map((row) => (
          <motion.li
            key={row.key}
            data-state={row.state}
            initial={{ height: 0, opacity: 0 }}
            animate={{ height: 'auto', opacity: 1 }}
            exit={{ height: 0, opacity: 0 }}
            transition={{ type: 'spring', stiffness: 440, damping: 42 }}
            className="overflow-clip [&+&]:shadow-[inset_0_0.5px_0_var(--separator)]"
          >
            <div className="flex items-start gap-2.5 py-2.5">
              <span className="mt-px flex shrink-0" aria-hidden="true">
                <StateIcon state={row.state} />
              </span>
              <div className="min-w-0 flex-1">
                <div className="flex items-baseline gap-3">
                  <span className="min-w-0 flex-1 truncate text-[0.875rem] leading-5">{row.name}</span>
                  <span className={cn('caption shrink-0 tabular', row.state === 'failed' && 'text-critical')}>{row.state === 'done' && row.detail ? row.detail : STATE_TEXT[row.state]}</span>
                </div>
                {row.state === 'failed' && row.message && <p className="text-[0.8125rem] leading-snug text-critical">{row.message}</p>}
              </div>
              {row.onRemove && row.state === 'failed' && (
                <IconButton label={`Remove ${row.name} from the list`} onClick={row.onRemove} className="-my-1.5 -mr-2 size-8 shrink-0">
                  <X size={14} aria-hidden="true" />
                </IconButton>
              )}
            </div>
          </motion.li>
        ))}
      </AnimatePresence>
    </ul>
  );
}

/** The highlight a card shows while PDFs are dragged over it: an accent outline and a quiet tint, never a glow. */
export function DropHighlight({ active, children = 'Drop PDFs to upload' }: { active: boolean; children?: ReactNode }) {
  return (
    <div
      aria-hidden="true"
      className={cn(
        'pointer-events-none absolute inset-0 z-10 flex items-center justify-center rounded-[inherit] bg-accent-soft shadow-[inset_0_0_0_2px_var(--accent)] transition-opacity duration-150',
        active ? 'opacity-100' : 'opacity-0',
      )}
    >
      <span className="rounded-full bg-accent px-3 py-1 text-[0.8125rem] font-medium text-accent-contrast">{children}</span>
    </div>
  );
}

/** A hidden multi-file PDF input and a function that opens it. The input resets after each pick. */
export function usePdfPicker(onFiles: (files: File[]) => void) {
  const input = useRef<HTMLInputElement>(null);
  const element = (
    <input
      ref={input}
      type="file"
      accept="application/pdf,.pdf"
      multiple
      className="sr-only"
      tabIndex={-1}
      aria-hidden="true"
      onChange={(event) => {
        const files = [...(event.target.files ?? [])];
        event.target.value = '';
        if (files.length > 0) onFiles(files);
      }}
    />
  );
  return { input: element, open: () => input.current?.click() };
}
