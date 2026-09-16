import { useSettings, useSession } from '@/api/queries';
import type { FormatPreferences } from '@/lib/format';

const DEFAULTS: FormatPreferences = { currency: 'CAD', dateFormat: 'MMM d, yyyy' };

/** Currency and date format for display. Summary responses also carry the currency they were computed in. */
export function usePreferences(): FormatPreferences {
  const session = useSession();
  const settings = useSettings(session.data?.authenticated === true);
  return settings.data ? { currency: settings.data.currency, dateFormat: settings.data.dateFormat } : DEFAULTS;
}
