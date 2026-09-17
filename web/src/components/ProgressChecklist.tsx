import { AnimatePresence, motion } from 'motion/react';
import { Check, Minus, X } from 'lucide-react';
import type { JobStep } from '@/api/schemas';
import { cn } from '@/lib/cn';

function StepIcon({ status }: { status: JobStep['status'] }) {
  switch (status) {
    case 'done':
      return (
        <motion.span
          initial={{ scale: 0.4, opacity: 0 }}
          animate={{ scale: 1, opacity: 1 }}
          transition={{ type: 'spring', stiffness: 520, damping: 26 }}
          className="flex size-5 items-center justify-center rounded-full bg-positive text-white"
        >
          <Check size={12} strokeWidth={3} />
        </motion.span>
      );
    case 'failed':
      return (
        <span className="flex size-5 items-center justify-center rounded-full bg-critical text-white">
          <X size={12} strokeWidth={3} />
        </span>
      );
    case 'skipped':
      return (
        <span className="flex size-5 items-center justify-center rounded-full bg-fill-strong text-label-secondary">
          <Minus size={12} strokeWidth={3} />
        </span>
      );
    case 'running':
      return <span className="block size-5 animate-spin rounded-full border-2 border-accent/25 border-t-accent" />;
    default:
      return <span className="block size-5 rounded-full border-[1.5px] border-dashed border-label-tertiary/60" />;
  }
}

const statusText: Record<JobStep['status'], string> = {
  pending: 'waiting',
  running: 'in progress',
  done: 'done',
  failed: 'failed',
  skipped: 'skipped',
};

/** Step-by-step progress for Gmail scans and statement processing, announced to screen readers as it changes. */
export function ProgressChecklist({ steps }: { steps: JobStep[] }) {
  return (
    <ol className="space-y-3" aria-live="polite">
      <AnimatePresence initial={false}>
        {steps.map((step) => (
          <motion.li
            key={step.key}
            layout="position"
            initial={{ opacity: 0, y: 4 }}
            animate={{ opacity: 1, y: 0 }}
            className="flex items-start gap-3"
          >
            <span className="mt-px flex" aria-hidden="true">
              <StepIcon status={step.status} />
            </span>
            <div className="min-w-0 flex-1">
              <p className={cn('text-[0.9375rem] leading-5', step.status === 'pending' ? 'text-label-secondary' : 'text-label')}>
                {step.label}
                <span className="sr-only">, {statusText[step.status]}</span>
              </p>
              {step.detail && (
                <p className={cn('caption mt-0.5', step.status === 'failed' && 'text-critical')}>{step.detail}</p>
              )}
            </div>
          </motion.li>
        ))}
      </AnimatePresence>
    </ol>
  );
}
