import { motion } from 'motion/react';
import { FileSearch, Info, Lock, ScanText, Sparkles } from 'lucide-react';
import { errorMessage } from '@/api/client';
import { useStartDemo } from '@/api/queries';
import type { Capabilities } from '@/api/schemas';
import { useSessionExpired } from '@/components/errors/sessionNotice';
import { Button, buttonStyles } from '@/components/ui/Button';
import { useSingleFlight } from '@/hooks/useSingleFlight';

const FEATURES = [
  {
    icon: FileSearch,
    title: 'Finds your statements',
    body: 'With read-only Gmail access, FinSight looks for bank and card statements. You choose which ones to analyze.',
  },
  {
    icon: ScanText,
    title: 'Reads every transaction',
    body: 'PDFs are read on the server, transactions are extracted and categorized, and transfers between your own accounts are left out.',
  },
  {
    icon: Sparkles,
    title: 'Explains the numbers',
    body: 'Totals are calculated precisely first. AI then explains the patterns, using only your figures.',
  },
];

const PRIVACY = [
  'Read-only Gmail access, used only to find statement emails and download their PDF attachments. FinSight never sends, deletes or changes email.',
  'PDFs are processed in memory and not kept. Transactions are stored encrypted, with card and account numbers masked to the last four digits.',
  'The AI receives totals and merchant names, never account numbers, email content or your identity.',
  'You can disconnect Gmail and delete all of your data at any time. Your information is never sold or shared.',
];

function GoogleMark() {
  return (
    <svg viewBox="0 0 48 48" width="18" height="18" aria-hidden="true">
      <path fill="#FFC107" d="M43.6 20.1H42V20H24v8h11.3C33.7 32.7 29.2 36 24 36c-6.6 0-12-5.4-12-12s5.4-12 12-12c3.1 0 5.8 1.2 7.9 3.1l5.7-5.7C34 6.1 29.3 4 24 4 12.9 4 4 12.9 4 24s8.9 20 20 20 20-8.9 20-20c0-1.3-.1-2.6-.4-3.9z" />
      <path fill="#FF3D00" d="m6.3 14.7 6.6 4.8C14.7 15.1 19 12 24 12c3.1 0 5.8 1.2 7.9 3.1l5.7-5.7C34 6.1 29.3 4 24 4 16.3 4 9.7 8.3 6.3 14.7z" />
      <path fill="#4CAF50" d="M24 44c5.2 0 9.9-2 13.4-5.2l-6.2-5.2C29.2 35.1 26.7 36 24 36c-5.2 0-9.6-3.3-11.3-8l-6.5 5C9.5 39.6 16.2 44 24 44z" />
      <path fill="#1976D2" d="M43.6 20.1H42V20H24v8h11.3c-.8 2.2-2.2 4.2-4.1 5.6l6.2 5.2C37 39.2 44 34 44 24c0-1.3-.1-2.6-.4-3.9z" />
    </svg>
  );
}

/**
 * The signed-out screen. On laptops and desktops it is one centred composition that fits the viewport without
 * scrolling: the pitch and sign-in on the left, what FinSight does and what it can access on the right.
 * Type and spacing tighten on short viewports; phones stack the two columns and may scroll.
 */
export function WelcomePage({ capabilities }: { capabilities: Capabilities }) {
  const authStatus = new URLSearchParams(window.location.search).get('auth');
  const demo = useStartDemo();
  const once = useSingleFlight();
  // The session ended while the app was open (a 401), rather than a first visit or choosing Sign out.
  const expired = useSessionExpired();

  return (
    <div className="flex min-h-dvh items-center justify-center px-5 pt-[max(2rem,env(safe-area-inset-top))] pb-8 sm:px-8 lg:py-8">
      <motion.main
        initial={{ opacity: 0, y: 10 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.45, ease: [0.2, 0.8, 0.2, 1] }}
        className="grid w-full max-w-[1100px] items-center gap-8 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.02fr)] lg:gap-14"
      >
        <section aria-labelledby="welcome-title">
          <img src="/favicon.svg" alt="" width={48} height={48} className="mb-5 size-12 rounded-[14px] shadow-float short:mb-4 short:size-11" />
          <h1 id="welcome-title" className="welcome-title font-bold tracking-[-0.035em]">
            See where your money goes.
          </h1>
          <p className="mt-3 max-w-md text-[1.125rem] leading-snug text-label-secondary short:text-[1.0625rem]">
            FinSight reads your bank statements and turns them into a clear picture of what you earn, spend and save.
          </p>

          {expired && (
            <p role="status" className="mt-5 flex items-start gap-2.5 rounded-xl bg-accent-soft px-4 py-3 text-[0.9375rem] text-label">
              <Info size={18} className="mt-px shrink-0 text-accent" aria-hidden="true" />
              You were signed out. Sign in again to continue.
            </p>
          )}
          {authStatus === 'failed' && (
            <p role="alert" className="mt-5 rounded-xl bg-critical-soft px-4 py-3 text-[0.9375rem] text-critical">
              Google sign-in didn’t complete. Try again.
            </p>
          )}
          {authStatus === 'unavailable' && (
            <p role="alert" className="mt-5 rounded-xl bg-attention-soft px-4 py-3 text-[0.9375rem] text-attention">
              Google sign-in isn’t set up on this server yet.
            </p>
          )}

          <div className="mt-7 flex flex-col gap-3 sm:flex-row short:mt-6">
            {capabilities.googleSignIn ? (
              <a href="/api/auth/google" className={buttonStyles({ size: 'lg', className: 'gap-2.5' })}>
                <span className="flex size-6 items-center justify-center rounded-full bg-white">
                  <GoogleMark />
                </span>
                Continue with Google
              </a>
            ) : (
              <Button size="lg" disabled>
                Continue with Google
              </Button>
            )}
            {capabilities.demo && (
              <Button size="lg" variant="secondary" loading={demo.isPending} onClick={() => void once(() => demo.mutateAsync())}>
                Explore with sample data
              </Button>
            )}
          </div>
          {demo.isError && (
            <p role="alert" className="mt-3 text-[0.9375rem] text-critical">
              {errorMessage(demo.error)}
            </p>
          )}
          {!capabilities.googleSignIn && <p className="caption mt-3">Google sign-in needs OAuth credentials on the server. See the README.</p>}
        </section>

        <section aria-label="About FinSight" className="card p-6 sm:p-7 short:p-5">
          <ul className="space-y-5 short:space-y-4">
            {FEATURES.map(({ icon: Icon, title, body }) => (
              <li key={title} className="flex gap-3.5">
                <span className="glass-control flex size-9 shrink-0 items-center justify-center rounded-[11px] text-accent">
                  <Icon size={18} aria-hidden="true" />
                </span>
                <div>
                  <h2 className="text-[1rem] font-semibold tracking-[-0.01em]">{title}</h2>
                  <p className="mt-0.5 text-[0.875rem] leading-snug text-label-secondary">{body}</p>
                </div>
              </li>
            ))}
          </ul>

          <div className="mt-6 border-t border-separator pt-5 short:mt-5 short:pt-4">
            <h2 className="flex items-center gap-2 text-[0.875rem] font-semibold">
              <Lock size={15} aria-hidden="true" className="text-label-secondary" />
              What FinSight accesses
            </h2>
            <ul className="mt-2 list-disc space-y-1 pl-5 text-[0.8125rem] leading-snug text-label-secondary marker:text-label-tertiary">
              {PRIVACY.map((item) => (
                <li key={item}>{item}</li>
              ))}
            </ul>
          </div>
        </section>
      </motion.main>
    </div>
  );
}
