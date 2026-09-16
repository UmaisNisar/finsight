import { useMutation, useQueryClient } from '@tanstack/react-query';
import { motion } from 'motion/react';
import { FileSearch, Lock, ScanText, Sparkles } from 'lucide-react';
import { errorMessage } from '@/api/client';
import { api } from '@/api/endpoints';
import { keys } from '@/api/queries';
import type { Capabilities } from '@/api/schemas';
import { Button } from '@/components/ui/Button';

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

export function WelcomePage({ capabilities }: { capabilities: Capabilities }) {
  const client = useQueryClient();
  const params = new URLSearchParams(window.location.search);
  const authStatus = params.get('auth');

  const demo = useMutation({
    mutationFn: api.startDemo,
    onSuccess: () => client.invalidateQueries({ queryKey: keys.session }),
  });

  return (
    <div className="flex min-h-dvh flex-col items-center px-5 pt-[max(3rem,env(safe-area-inset-top))] pb-10 sm:justify-center">
      <motion.main
        initial={{ opacity: 0, y: 12 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.5, ease: [0.2, 0.8, 0.2, 1] }}
        className="w-full max-w-[560px]"
      >
        <img src="/favicon.svg" alt="" className="mb-7 size-16 rounded-[18px] shadow-float" />
        <h1 className="text-[2.75rem] leading-[1.05] font-bold tracking-[-0.035em] sm:text-[3.25rem]">
          See where your
          <br />
          money goes.
        </h1>
        <p className="mt-4 max-w-md text-[1.1875rem] leading-snug text-label-secondary">
          FinSight reads your bank statements and turns them into a clear picture of what you earn, spend and save.
        </p>

        {authStatus === 'failed' && (
          <p role="alert" className="mt-6 rounded-xl bg-critical-soft px-4 py-3 text-[0.9375rem] text-critical">
            Google sign-in didn’t complete. Try again.
          </p>
        )}
        {authStatus === 'unavailable' && (
          <p role="alert" className="mt-6 rounded-xl bg-attention-soft px-4 py-3 text-[0.9375rem] text-attention">
            Google sign-in isn’t set up on this server yet.
          </p>
        )}

        <div className="mt-9 flex flex-col gap-3 sm:flex-row">
          {capabilities.googleSignIn ? (
            <a
              href="/api/auth/google"
              className="inline-flex h-12 items-center justify-center gap-2.5 rounded-full bg-label px-6 text-base font-medium text-canvas transition-transform active:scale-[0.98]"
            >
              <span className="flex size-6 items-center justify-center rounded-full bg-white">
                <GoogleMark />
              </span>
              Continue with Google
            </a>
          ) : (
            <Button size="lg" disabled title="Add Google OAuth credentials to the server to enable sign-in">
              Continue with Google
            </Button>
          )}
          {capabilities.demo && (
            <Button size="lg" variant="secondary" loading={demo.isPending} onClick={() => demo.mutate()}>
              Explore with sample data
            </Button>
          )}
        </div>
        {demo.isError && (
          <p role="alert" className="mt-3 text-[0.9375rem] text-critical">
            {errorMessage(demo.error)}
          </p>
        )}
        {!capabilities.googleSignIn && (
          <p className="caption mt-3">Google sign-in needs OAuth credentials on the server. See the README.</p>
        )}

        <ul className="mt-12 space-y-6">
          {FEATURES.map(({ icon: Icon, title, body }) => (
            <li key={title} className="flex gap-4">
              <span className="flex size-10 shrink-0 items-center justify-center rounded-xl bg-surface text-accent shadow-soft">
                <Icon size={20} aria-hidden="true" />
              </span>
              <div>
                <h2 className="text-[1.0625rem] font-semibold tracking-[-0.01em]">{title}</h2>
                <p className="mt-0.5 text-[0.9375rem] text-label-secondary">{body}</p>
              </div>
            </li>
          ))}
        </ul>

        <section aria-labelledby="privacy-title" className="mt-12 rounded-2xl bg-surface p-5 shadow-soft">
          <h2 id="privacy-title" className="flex items-center gap-2 text-[0.9375rem] font-semibold">
            <Lock size={16} aria-hidden="true" className="text-label-secondary" />
            What FinSight accesses
          </h2>
          <ul className="mt-2 list-disc space-y-1.5 pl-5 text-[0.875rem] text-label-secondary marker:text-label-tertiary">
            <li>Read-only Gmail access, used only to find statement emails and download their PDF attachments. FinSight never sends, deletes or changes email.</li>
            <li>PDFs are processed in memory and not kept. Transactions are stored encrypted, with card and account numbers masked to the last four digits.</li>
            <li>The AI receives totals and merchant names, never account numbers, email content or your identity.</li>
            <li>You can disconnect Gmail and delete all of your data at any time. Your information is never sold or shared.</li>
          </ul>
        </section>
      </motion.main>
    </div>
  );
}
