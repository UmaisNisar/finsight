import { useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router';
import { DEFAULT_PERIOD, isPreset, PERIOD_PARAMS, type PeriodSelection } from '@/lib/period';

export const PERIOD_STORAGE_KEY = 'finsight.period';

function readStored(): PeriodSelection {
  try {
    const raw = localStorage.getItem(PERIOD_STORAGE_KEY);
    return raw && isPreset(raw) && raw !== 'custom' ? { preset: raw } : DEFAULT_PERIOD;
  } catch {
    return DEFAULT_PERIOD;
  }
}

/**
 * The selected time period lives in the URL (?period=last-3-months, or custom with from/to), so every
 * screen agrees on it, it survives reloads and it can be shared. The last preset is remembered.
 */
export function usePeriod() {
  const [params, setParams] = useSearchParams();

  const period = useMemo<PeriodSelection>(() => {
    const preset = params.get('period');
    if (!isPreset(preset)) {
      return readStored();
    }
    if (preset === 'custom') {
      const from = params.get('from');
      const to = params.get('to');
      return from && to && from <= to ? { preset, from, to } : readStored();
    }
    return { preset };
  }, [params]);

  const setPeriod = useCallback(
    (next: PeriodSelection) => {
      setParams(
        (current) => {
          const updated = new URLSearchParams(current);
          updated.set('period', next.preset);
          if (next.preset === 'custom' && next.from && next.to) {
            updated.set('from', next.from);
            updated.set('to', next.to);
          } else {
            updated.delete('from');
            updated.delete('to');
            try {
              localStorage.setItem(PERIOD_STORAGE_KEY, next.preset);
            } catch {
              // Storage can be unavailable (private mode); the URL still carries the choice.
            }
          }
          return updated;
        },
        { replace: true },
      );
    },
    [setParams],
  );

  return { period, setPeriod };
}

/**
 * Builds in-app links that keep the selected period, so moving between screens never resets the time range.
 * `to` may carry its own query (`/transactions?group=food`); period parameters are appended.
 */
export function usePeriodLink() {
  const [params] = useSearchParams();
  return useCallback(
    (to: string) => {
      const [path, query] = to.split('?') as [string, string | undefined];
      const next = new URLSearchParams(query);
      for (const key of PERIOD_PARAMS) {
        const value = params.get(key);
        if (value) next.set(key, value);
      }
      const suffix = next.toString();
      return suffix ? `${path}?${suffix}` : path;
    },
    [params],
  );
}
