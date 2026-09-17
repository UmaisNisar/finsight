import { useEffect, useEffectEvent, useRef } from 'react';
import { useSearchParams } from 'react-router';
import { parseGmailOutcome, type GmailOutcome } from '@/lib/gmail';

/**
 * Finishes the Google consent round trip: when the page loads with `?gmail=<outcome>`, calls `onReturn` once and
 * removes the parameter, so a refresh or a shared link doesn't repeat it.
 */
export function useGmailReturn(onReturn: (outcome: GmailOutcome) => void) {
  const [params, setParams] = useSearchParams();
  const status = params.get('gmail');
  const handled = useRef(false);
  const handle = useEffectEvent(onReturn);

  useEffect(() => {
    if (!status || handled.current) return;
    handled.current = true;
    handle(parseGmailOutcome(status));
    setParams(
      (current) => {
        const next = new URLSearchParams(current);
        next.delete('gmail');
        return next;
      },
      { replace: true },
    );
  }, [status, setParams]);
}
