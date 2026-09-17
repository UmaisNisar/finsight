import type { Anomaly, Recurring } from '@/api/schemas';
import { formatMoney } from './format';

/** Human wording for API enums, shared so every screen describes the same thing the same way. */
export const FREQUENCY_LABEL: Record<Recurring['frequency'], string> = {
  weekly: 'Weekly',
  biweekly: 'Every 2 weeks',
  monthly: 'Monthly',
  quarterly: 'Quarterly',
  annual: 'Yearly',
};

/** Why a transaction was flagged as unusual, in one short phrase. */
export function anomalyReason(anomaly: Anomaly, currency: string): string {
  switch (anomaly.kind) {
    case 'possibleDuplicate':
      return 'Possible duplicate charge';
    case 'newMerchant':
      return 'Large payment to a new merchant';
    default:
      return anomaly.typicalAmount && anomaly.typicalAmount > 0
        ? `Usually about ${formatMoney(anomaly.typicalAmount, currency, { whole: true })}`
        : 'Larger than usual';
  }
}
