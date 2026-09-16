import { AnimatePresence, motion } from 'motion/react';
import { X } from 'lucide-react';
import { useJobs } from '@/app/providers/JobsProvider';
import { ProgressChecklist } from './ProgressChecklist';
import { IconButton } from './ui/Button';

const titles = {
  sync: { active: 'Finding statements', done: 'Gmail scan finished' },
  process: { active: 'Analyzing statements', done: 'Analysis finished' },
  upload: { active: 'Reading your statement', done: 'Statement processed' },
} as const;

/** Floating progress for background work. The user can keep browsing while statements process. */
export function JobProgressPanel() {
  const { job, isActive, dismiss } = useJobs();

  return (
    <AnimatePresence>
      {job && (
        <motion.aside
          aria-label="Background progress"
          initial={{ opacity: 0, y: 16, scale: 0.98 }}
          animate={{ opacity: 1, y: 0, scale: 1 }}
          exit={{ opacity: 0, y: 16, scale: 0.98 }}
          transition={{ type: 'spring', stiffness: 380, damping: 32 }}
          className="glass fixed right-4 bottom-[calc(5.5rem+env(safe-area-inset-bottom))] left-4 z-40 max-h-[60dvh] overflow-y-auto rounded-3xl p-5 shadow-float md:right-6 md:bottom-6 md:left-auto md:w-[360px]"
        >
          <div className="mb-4 flex items-center justify-between gap-2">
            <h2 className="text-[1.0625rem] font-semibold tracking-[-0.01em]">
              {job.status === 'failed' ? 'Something went wrong' : isActive ? titles[job.kind].active : titles[job.kind].done}
            </h2>
            {!isActive && (
              <IconButton label="Dismiss" onClick={dismiss} className="-mr-2 size-8">
                <X size={16} />
              </IconButton>
            )}
          </div>
          <ProgressChecklist steps={job.steps} />
          {job.status === 'failed' && job.errorMessage && <p className="mt-4 text-[0.875rem] text-critical">{job.errorMessage}</p>}
        </motion.aside>
      )}
    </AnimatePresence>
  );
}
