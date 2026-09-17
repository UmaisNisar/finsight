import { HelpCircle, Landmark, Mail } from 'lucide-react';
import { useId, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { Collapse } from '@/components/ui/AutoHeight';
import { Button } from '@/components/ui/Button';
import { cn } from '@/lib/cn';
import { STATEMENT_SOURCES, type StatementSource } from './steps';

export const SOURCE_COPY: Record<StatementSource, { title: string; description: string; summary: string; icon: ReactNode }> = {
  email: {
    title: 'By email',
    description: 'My bank emails statements, or tells me when one is ready.',
    summary: 'By email',
    icon: <Mail size={20} />,
  },
  download: {
    title: 'I download them from my bank’s website',
    description: 'I sign in to online banking to get PDFs.',
    summary: 'Downloaded from your bank’s website',
    icon: <Landmark size={20} />,
  },
  unsure: {
    title: 'Not sure',
    description: 'FinSight can check your inbox for you.',
    summary: 'Not sure, so FinSight will check your inbox',
    icon: <HelpCircle size={20} />,
  },
};

/**
 * "How do you get your statements?" as large tappable rows in one grouped list, like an iOS choice list. It's an
 * ARIA radio group: arrow keys move the selection, and Continue commits it.
 */
export function SourceStep({
  value,
  onChoose,
  onCancel,
  onSelect,
}: {
  value: StatementSource | null;
  onChoose: (source: StatementSource) => void;
  onCancel?: () => void;
  /** The highlighted option before Continue, so the steps below can preview that path. */
  onSelect?: (source: StatementSource) => void;
}) {
  const [selected, setSelectedState] = useState<StatementSource | null>(value);
  const setSelected = (source: StatementSource) => {
    setSelectedState(source);
    onSelect?.(source);
  };
  const labelId = useId();
  const refs = useRef<Partial<Record<StatementSource, HTMLButtonElement | null>>>({});

  function onKeyDown(event: KeyboardEvent<HTMLButtonElement>, source: StatementSource) {
    const delta = event.key === 'ArrowDown' || event.key === 'ArrowRight' ? 1 : event.key === 'ArrowUp' || event.key === 'ArrowLeft' ? -1 : 0;
    if (delta === 0) return;
    event.preventDefault();
    const index = STATEMENT_SOURCES.indexOf(source);
    const next = STATEMENT_SOURCES[(index + delta + STATEMENT_SOURCES.length) % STATEMENT_SOURCES.length] as StatementSource;
    setSelected(next);
    refs.current[next]?.focus();
  }

  // Roving tab stop: the selected row, or the first when nothing is selected.
  const tabStop = selected ?? STATEMENT_SOURCES[0];

  return (
    <>
      <p id={labelId} className="text-[0.9375rem] leading-relaxed text-label-secondary">
        FinSight sets up differently depending on where your statements come from. You can change this later.
      </p>
      <div role="radiogroup" aria-labelledby={labelId} className="card grouped mt-4 overflow-hidden rounded-[20px]">
        {STATEMENT_SOURCES.map((source) => {
          const copy = SOURCE_COPY[source];
          const checked = selected === source;
          return (
            <button
              key={source}
              ref={(element) => {
                refs.current[source] = element;
              }}
              type="button"
              role="radio"
              aria-checked={checked}
              tabIndex={source === tabStop ? 0 : -1}
              onClick={() => setSelected(source)}
              onKeyDown={(event) => onKeyDown(event, source)}
              className="flex w-full items-center gap-3.5 px-4 py-3.5 text-left transition-colors hover:bg-fill focus-visible:outline-offset-[-3px] md:px-5"
            >
              <span className={cn('flex size-10 shrink-0 items-center justify-center rounded-xl transition-colors duration-200', checked ? 'bg-accent text-accent-contrast' : 'bg-fill text-label-secondary')} aria-hidden="true">
                {copy.icon}
              </span>
              <span className="min-w-0 flex-1">
                <span className="block text-[0.9375rem] leading-5 font-medium">{copy.title}</span>
                <span className="mt-0.5 block text-[0.8125rem] leading-snug text-label-secondary">{copy.description}</span>
              </span>
              <span
                aria-hidden="true"
                className={cn(
                  'flex size-[22px] shrink-0 items-center justify-center rounded-full border-[1.5px] transition-[border-color,background-color] duration-200',
                  checked ? 'border-accent bg-accent' : 'border-label-tertiary/60',
                )}
              >
                <span className={cn('size-2 rounded-full bg-white transition-[opacity,transform] duration-200', checked ? 'scale-100 opacity-100' : 'scale-50 opacity-0')} />
              </span>
            </button>
          );
        })}
      </div>

      <Collapse open={selected === 'unsure'}>
        <p className="px-1 pt-3 text-[0.875rem] leading-snug text-label-secondary">
          Connect Gmail and FinSight will look for statement emails. If your bank doesn’t send them, you can switch to downloading PDFs in one tap.
        </p>
      </Collapse>

      <div className="mt-5 flex flex-col gap-2 *:w-full sm:flex-row sm:flex-wrap sm:items-center sm:*:w-auto">
        <Button disabled={selected === null} onClick={() => selected && onChoose(selected)}>
          Continue
        </Button>
        {onCancel && (
          <Button variant="plain" onClick={onCancel}>
            Cancel
          </Button>
        )}
      </div>
    </>
  );
}
