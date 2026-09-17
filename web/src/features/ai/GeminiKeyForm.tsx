import { ArrowUpRight, Eye, EyeOff } from 'lucide-react';
import { useId, useState, type ReactNode } from 'react';
import { ApiError, errorMessage } from '@/api/client';
import { useSaveAiKey } from '@/api/queries';
import type { AiKey } from '@/api/schemas';
import { Button, IconButton } from '@/components/ui/Button';
import { Collapse } from '@/components/ui/AutoHeight';
import { useRetained } from '@/hooks/useRetained';
import { useSingleFlight } from '@/hooks/useSingleFlight';
import { cn } from '@/lib/cn';

export const AI_STUDIO_URL = 'https://aistudio.google.com/apikey';

/** What went wrong, in words that say what to do next. */
export function aiKeyErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code === 'invalid_api_key') return 'Google didn’t accept that key. Check that you copied all of it, then try again.';
    // Either Google throttled the key or FinSight's own limit applies; the server says which.
    if (error.code === 'rate_limited') return error.message;
    if (error.code === 'ai_unavailable') return 'FinSight couldn’t reach Google to check the key. Try again in a moment.';
    if (error.status === 403 || error.code === 'demo_mode') return 'The demo can’t save an API key. Sign in with Google to use your own.';
  }
  return errorMessage(error);
}

function Guide() {
  const steps: ReactNode[] = [
    <>
      Open{' '}
      <a href={AI_STUDIO_URL} target="_blank" rel="noreferrer" className="inline-flex items-center gap-0.5 font-medium text-accent hover:underline">
        Google AI Studio
        <ArrowUpRight size={14} strokeWidth={2.25} aria-hidden="true" />
        <span className="sr-only">(opens in a new tab)</span>
      </a>{' '}
      and sign in with your Google account.
    </>,
    <>
      Click <span className="font-medium">Create API key</span>.
    </>,
    <>Copy the key, then paste it below.</>,
  ];
  return (
    <ol className="space-y-2.5">
      {steps.map((step, i) => (
        <li key={i} className="flex gap-3 text-[0.9375rem] leading-[22px]">
          <span aria-hidden="true" className="tabular flex size-[22px] shrink-0 items-center justify-center rounded-full bg-fill text-[0.75rem] font-semibold text-label-secondary">
            {i + 1}
          </span>
          <span className="min-w-0">{step}</span>
        </li>
      ))}
    </ol>
  );
}

/**
 * Guides the user to a Gemini API key and saves it. The server checks the key with Google before storing it
 * (encrypted), so success here means AI will work. Shared by onboarding and Settings.
 */
export interface GeminiKeyFormProps {
  onSaved?: (key: AiKey) => void;
  /** Shows a Cancel button beside Verify & save. */
  onCancel?: () => void;
  /** Tighter spacing for sheets and narrow panels. */
  compact?: boolean;
  /** Another button beside Verify & save, such as Skip. */
  secondaryAction?: ReactNode;
  submitLabel?: string;
}

export function GeminiKeyForm({ onSaved, onCancel, compact = false, secondaryAction, submitLabel = 'Verify & save' }: GeminiKeyFormProps) {
  const [value, setValue] = useState('');
  const [shown, setShown] = useState(false);
  const save = useSaveAiKey();
  const once = useSingleFlight();
  const inputId = useId();
  const errorId = useId();
  const noteId = useId();
  const apiKey = value.trim();
  // The message stays while its row collapses away after the user starts typing again.
  const error = useRetained(save.isError ? aiKeyErrorMessage(save.error) : null);

  return (
    <div className={compact ? 'space-y-4' : 'space-y-5'}>
      <Guide />
      <form
        className="space-y-3"
        onSubmit={(event) => {
          event.preventDefault();
          if (!apiKey) return;
          void once(async () => {
            const key = await save.mutateAsync(apiKey);
            onSaved?.(key);
          });
        }}
      >
        <label htmlFor={inputId} className="eyebrow block px-1">
          Gemini API key
        </label>
        <div className="relative">
          <input
            id={inputId}
            type={shown ? 'text' : 'password'}
            value={value}
            onChange={(e) => {
              setValue(e.target.value);
              if (save.isError) save.reset();
            }}
            placeholder="Paste your key"
            autoComplete="off"
            autoCapitalize="off"
            autoCorrect="off"
            spellCheck={false}
            aria-invalid={save.isError || undefined}
            aria-describedby={cn(save.isError && errorId, noteId) || undefined}
            className={cn(
              'glass-control h-12 w-full rounded-full pr-14 pl-5 text-[0.9375rem] placeholder:font-sans placeholder:text-label-tertiary',
              value && 'font-mono tracking-tight',
              save.isError && 'shadow-[inset_0_0_0_1.5px_var(--critical)]',
            )}
          />
          <IconButton label={shown ? 'Hide key' : 'Show key'} aria-pressed={shown} onClick={() => setShown(!shown)} className="absolute top-1/2 right-1.5 -translate-y-1/2">
            {shown ? <EyeOff size={18} aria-hidden="true" /> : <Eye size={18} aria-hidden="true" />}
          </IconButton>
        </div>
        <Collapse open={save.isError}>
          <p id={errorId} role="alert" className="px-1 pb-0.5 text-[0.875rem] text-critical">
            {error.item}
          </p>
        </Collapse>
        <p id={noteId} className="caption px-1">
          Free to start. Your key is stored encrypted on the server and never shown again, only its last four characters. You can remove it anytime in Settings.
        </p>
        <div className={cn('flex flex-col gap-2 *:w-full sm:flex-row sm:flex-wrap sm:items-center sm:*:w-auto', compact ? 'pt-1' : 'pt-2')}>
          <Button type="submit" disabled={!apiKey} loading={save.isPending}>
            {save.isPending ? 'Verifying…' : submitLabel}
          </Button>
          {secondaryAction}
          {onCancel && (
            <Button variant="plain" onClick={onCancel}>
              Cancel
            </Button>
          )}
        </div>
      </form>
    </div>
  );
}
