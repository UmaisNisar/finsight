import { Eye, EyeOff, FileUp, Lock } from 'lucide-react';
import { useId, useRef, useState } from 'react';
import { useSingleFlight } from '@/hooks/useSingleFlight';
import { cn } from '@/lib/cn';
import { Collapse } from './ui/AutoHeight';
import { Button, IconButton } from './ui/Button';

export const PASSWORD_HINT = 'This PDF is password-protected. Banks often use part of your date of birth or account number.';
export const WRONG_PASSWORD = 'That password didn’t unlock it. Check it and try again.';

interface PdfPasswordPromptProps {
  /** The last password tried didn't open the file. */
  incorrect?: boolean;
  /** Why the last attempt failed when it wasn't the password (the connection dropped, say). */
  error?: string | null;
  /** Sends the file with this password. The prompt clears its field once this resolves. */
  onUnlock: (password: string) => Promise<unknown>;
  /** The file is being sent or read. */
  busy?: boolean;
  /** The file isn't held any more (the page was reloaded): it has to be chosen again before it can be unlocked. */
  fileMissing?: boolean;
  onChooseFile?: () => void;
  compact?: boolean;
  className?: string;
}

/**
 * An inline password field for a locked PDF, with show/hide and Unlock. The field is uncontrolled and emptied after each
 * attempt, so the password is never held in React state, a query cache or storage; it goes straight into the upload
 * request.
 */
export function PdfPasswordPrompt({ incorrect = false, error = null, onUnlock, busy = false, fileMissing = false, onChooseFile, compact = false, className }: PdfPasswordPromptProps) {
  const input = useRef<HTMLInputElement>(null);
  const [filled, setFilled] = useState(false);
  const [shown, setShown] = useState(false);
  const [sending, setSending] = useState(false);
  const once = useSingleFlight();
  const inputId = useId();
  const hintId = useId();
  const errorId = useId();

  return (
    <form
      className={cn('space-y-2', className)}
      onSubmit={(event) => {
        event.preventDefault();
        const field = input.current;
        const password = field?.value ?? '';
        if (!password || fileMissing || busy) return;
        void once(async () => {
          setSending(true);
          try {
            await onUnlock(password);
          } finally {
            if (field) field.value = '';
            setFilled(false);
            setShown(false);
            setSending(false);
          }
        });
      }}
    >
      <p id={hintId} className={cn('flex gap-1.5 leading-snug text-label-secondary', compact ? 'text-[0.8125rem]' : 'text-[0.875rem]')}>
        <Lock size={compact ? 13 : 14} className="mt-[0.2em] shrink-0 text-label-tertiary" aria-hidden="true" />
        <span>{PASSWORD_HINT}</span>
      </p>
      <Collapse open={(incorrect || !!error) && !busy}>
        <p id={errorId} role="alert" className={cn('leading-snug text-critical', compact ? 'text-[0.8125rem]' : 'text-[0.875rem]')}>
          {incorrect ? WRONG_PASSWORD : error}
        </p>
      </Collapse>
      <div className="flex items-center gap-2">
        <div className="relative min-w-0 flex-1">
          <label htmlFor={inputId} className="sr-only">
            PDF password
          </label>
          <input
            ref={input}
            id={inputId}
            type={shown ? 'text' : 'password'}
            placeholder="Password"
            autoComplete="off"
            autoCapitalize="off"
            autoCorrect="off"
            spellCheck={false}
            disabled={fileMissing}
            aria-invalid={incorrect || undefined}
            aria-describedby={cn(hintId, (incorrect || !!error) && errorId)}
            onChange={(event) => setFilled(event.target.value.length > 0)}
            className={cn(
              'glass-control h-10 w-full rounded-full pr-11 pl-4 text-[0.9375rem] placeholder:text-label-tertiary disabled:opacity-45',
              incorrect && 'shadow-[inset_0_0_0_1.5px_var(--critical)]',
            )}
          />
          <IconButton
            label={shown ? 'Hide password' : 'Show password'}
            aria-pressed={shown}
            disabled={fileMissing}
            onClick={() => setShown(!shown)}
            className="absolute top-1/2 right-1 size-8 -translate-y-1/2"
          >
            {shown ? <EyeOff size={16} aria-hidden="true" /> : <Eye size={16} aria-hidden="true" />}
          </IconButton>
        </div>
        <Button type="submit" disabled={!filled || fileMissing} loading={sending || busy}>
          Unlock
        </Button>
      </div>
      <Collapse open={fileMissing}>
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2 pt-0.5">
          <p className="caption min-w-0 flex-1">FinSight doesn’t keep your files. Choose this PDF again to unlock it.</p>
          {onChooseFile && (
            <Button variant="secondary" size="sm" icon={<FileUp size={14} aria-hidden="true" />} onClick={onChooseFile}>
              Choose file
            </Button>
          )}
        </div>
      </Collapse>
    </form>
  );
}
