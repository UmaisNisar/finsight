import { WidgetBoundary } from '@/components/errors/WidgetBoundary';
import { AlertCircle, Check, Info, Minus } from 'lucide-react';
import { AnimatePresence, motion } from 'motion/react';
import type { ReactNode } from 'react';
import { Collapse } from '@/components/ui/AutoHeight';
import { cn } from '@/lib/cn';
import type { StepState } from './steps';

const POP = { type: 'spring', stiffness: 520, damping: 28 } as const;
const HEIGHT_SPRING = { type: 'spring', stiffness: 440, damping: 42 } as const;

/** The step's number, a check when done, or a dash when skipped. The shapes crossfade as the state changes. */
function StepIndicator({ number, state }: { number: number; state: StepState }) {
  const glyph =
    state === 'done' ? (
      <Check size={15} strokeWidth={3} />
    ) : state === 'skipped' ? (
      <Minus size={15} strokeWidth={3} />
    ) : (
      <span className="tabular text-[0.8125rem] font-semibold">{number}</span>
    );
  return (
    <span
      aria-hidden="true"
      className={cn(
        'relative flex size-7 shrink-0 items-center justify-center rounded-full transition-[background-color,color,box-shadow] duration-300',
        state === 'done' && 'bg-positive text-white',
        state === 'skipped' && 'bg-fill-strong text-label-secondary',
        state === 'current' && 'bg-accent text-accent-contrast',
        state === 'upcoming' && 'text-label-tertiary shadow-[inset_0_0_0_1.5px_color-mix(in_srgb,var(--label-tertiary)_45%,transparent)]',
      )}
    >
      <AnimatePresence initial={false} mode="popLayout">
        <motion.span
          key={state === 'done' || state === 'skipped' ? state : `number-${number}`}
          className="flex items-center justify-center"
          initial={{ scale: 0.4, opacity: 0 }}
          animate={{ scale: 1, opacity: 1 }}
          exit={{ scale: 0.4, opacity: 0, transition: { duration: 0.12 } }}
          transition={POP}
        >
          {glyph}
        </motion.span>
      </AnimatePresence>
    </span>
  );
}

const STATE_TEXT: Record<StepState, string> = { done: 'Done', skipped: 'Skipped', current: 'Current step', upcoming: 'Not started' };

interface StepCardProps {
  id: string;
  number: number;
  title: string;
  /** What the step is for; shown until it is finished. */
  description: string;
  /** What happened; shown once it is finished or skipped. */
  summary?: ReactNode;
  state: StepState;
  /** A small button on a finished step, for revisiting it. */
  action?: ReactNode;
  children: ReactNode;
}

/**
 * One step of setup. Every card keeps the same two-line header whatever its state, so finishing a step only
 * changes the check and the caption; the current step's body opens below with a height spring.
 * Upcoming steps are dimmed through their text colour, never opacity, which would flatten the glass.
 * When the path changes, steps that join or leave the list unfold and fold by height (with the gap below them), so
 * the cards around them glide instead of jumping. Render inside AnimatePresence.
 */
export function StepCard({ id, number, title, description, summary, state, action, children }: StepCardProps) {
  const current = state === 'current';
  const finished = state === 'done' || state === 'skipped';
  const caption = finished && summary ? summary : description;

  return (
    <motion.li
      aria-current={current ? 'step' : undefined}
      aria-labelledby={`${id}-title`}
      data-state={state}
      initial={{ height: 0, overflow: 'clip' }}
      animate={{ height: 'auto', transitionEnd: { overflow: 'visible' } }}
      exit={{ height: 0, overflow: 'clip' }}
      transition={HEIGHT_SPRING}
    >
      <div className="pb-3">
        <div className="card rounded-[26px]">
          <div className="flex min-h-[76px] items-center gap-3.5 px-5 py-4 md:px-6">
            <StepIndicator number={number} state={state} />
            <div className="min-w-0 flex-1">
              <h2 id={`${id}-title`} tabIndex={-1} className={cn('text-[1.0625rem] leading-6 font-semibold tracking-[-0.012em] outline-none transition-colors duration-300', state === 'upcoming' && 'text-label-tertiary')}>
                {title}
                <span className="sr-only">, {STATE_TEXT[state]}</span>
              </h2>
              <p key={finished ? 'summary' : 'description'} className={cn('fade-in truncate text-[0.875rem] leading-5', state === 'upcoming' ? 'text-label-tertiary' : 'text-label-secondary')}>
                {caption}
              </p>
            </div>
            {finished && action}
          </div>
          <Collapse open={current}>
            <div className="px-5 pt-1 pb-6 md:pr-6 md:pl-[4.125rem]">
              {/* A failing step shows a retry inside its own card; the rest of onboarding keeps working. */}
              <WidgetBoundary name={`onboarding-${id}`} message="This step couldn’t be shown." queryKeys={[['statements'], ['gmail'], ['ai-key']]}>
                {children}
              </WidgetBoundary>
            </div>
          </Collapse>
        </div>
      </div>
    </motion.li>
  );
}

type NoticeTone = 'neutral' | 'attention' | 'critical';

const NOTICE_TONE: Record<NoticeTone, { box: string; icon: ReactNode }> = {
  neutral: { box: 'bg-fill text-label', icon: <Info size={18} className="text-label-secondary" aria-hidden="true" /> },
  attention: { box: 'bg-attention-soft text-label', icon: <AlertCircle size={18} className="text-attention" aria-hidden="true" /> },
  critical: { box: 'bg-critical-soft text-label', icon: <AlertCircle size={18} className="text-critical" aria-hidden="true" /> },
};

/** An inline message inside a step: what happened and, in the step's buttons below it, what to do about it. */
export function Notice({ tone = 'neutral', title, children, role }: { tone?: NoticeTone; title?: string; children: ReactNode; role?: 'alert' | 'status' }) {
  const style = NOTICE_TONE[tone];
  return (
    <div role={role} className={cn('fade-in flex gap-3 rounded-[18px] px-4 py-3', style.box)}>
      <span className="mt-px shrink-0">{style.icon}</span>
      <div className="min-w-0 text-[0.875rem] leading-snug">
        {title && <p className="font-semibold">{title}</p>}
        <div className={cn(title && 'mt-0.5', 'text-label-secondary')}>{children}</div>
      </div>
    </div>
  );
}
