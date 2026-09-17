/** Where "Connect Gmail" goes: Google's consent screen, then back to `returnTo` with `?gmail=<outcome>`. */
export function gmailConnectUrl(returnTo: string): string {
  return `/api/gmail/connect?returnTo=${encodeURIComponent(returnTo)}`;
}

export const GMAIL_OUTCOMES = ['connected', 'denied', 'failed', 'unavailable', 'demo'] as const;
export type GmailOutcome = (typeof GMAIL_OUTCOMES)[number];

export function parseGmailOutcome(value: string): GmailOutcome {
  return (GMAIL_OUTCOMES as readonly string[]).includes(value) ? (value as GmailOutcome) : 'failed';
}

/** One-line messages for each outcome, for toasts. Onboarding explains them inline in more detail. */
export const GMAIL_OUTCOME_MESSAGES: Record<GmailOutcome, { text: string; tone: 'success' | 'error' }> = {
  connected: { text: 'Gmail connected. Looking for statements…', tone: 'success' },
  denied: { text: 'FinSight needs permission to read Gmail to find statements. Nothing was connected.', tone: 'error' },
  failed: { text: 'Connecting Gmail didn’t complete. Try again.', tone: 'error' },
  unavailable: { text: 'Gmail integration isn’t set up on this server.', tone: 'error' },
  demo: { text: 'Demo mode uses sample statements. Sign in with Google to use your Gmail.', tone: 'error' },
};
