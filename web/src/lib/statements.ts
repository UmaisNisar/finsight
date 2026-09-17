import type { Statement } from '@/api/schemas';
import { formatMonth, formatShortDate } from './format';

/** Below this, a Gmail match is shown but not selected, with a "weak match" label. */
export const WEAK_MATCH_CONFIDENCE = 0.6;

/** Discovered statements start selected, except likely pay stubs and weak matches. */
export function isPreselected(statement: Statement): boolean {
  return statement.documentKind !== 'incomeDocument' && statement.detectionConfidence >= WEAK_MATCH_CONFIDENCE;
}

/** A short label for a discovered item that probably isn't a statement, or null when it looks like one. */
export function matchCaveat(statement: Statement): string | null {
  if (statement.documentKind === 'incomeDocument') return 'Possible pay stub';
  if (statement.detectionConfidence < WEAK_MATCH_CONFIDENCE) return 'Weak match';
  return null;
}

/** The name to lead a discovered item with: the bank if recognised, otherwise the sender. */
export function discoveredName(statement: Statement): string {
  return statement.institution ?? statement.senderName ?? 'Possible statement';
}

export const isInProgress = (statement: Statement) => statement.status === 'downloading' || statement.status === 'processing';

// ---------------------------------------------------------------------------------------------------------------
// Statement alerts: "your statement is ready" emails with no PDF attached.

export const isAlert = (statement: Statement) => statement.status === 'awaitingUpload';

/**
 * Began as an alert, whether or not its PDF has been uploaded since. The server marks these as needing an upload to
 * reprocess, which Gmail attachments never do.
 */
export const isAlertOrigin = (statement: Statement) => statement.source === 'gmail' && (isAlert(statement) || statement.reprocessNeedsUpload);

/** Alerts for one account. Banks such as CIBC send one a month, and each month is downloaded as its own PDF. */
export interface AlertGroup {
  key: string;
  /** "CIBC credit card ending 5190". */
  title: string;
  institution: string | null;
  signInUrl: string | null;
  downloadHint: string | null;
  /** Oldest first. */
  alerts: Statement[];
}

export const GENERIC_DOWNLOAD_HINT = 'Sign in to your bank, download the statement as a PDF, then upload it here.';
export const SEVERAL_PDFS_HINT = 'One PDF per month is fine; upload them all at once.';

const ACCOUNT_NOUN: Record<Statement['accountType'], string> = {
  unknown: 'account',
  chequing: 'chequing account',
  savings: 'savings account',
  creditCard: 'credit card',
  lineOfCredit: 'line of credit',
  investment: 'investment account',
};

/** The date an alert's statement belongs to: the end of its period when known, otherwise when the email arrived. */
const alertDate = (statement: Statement) => statement.periodEnd ?? statement.receivedAt ?? '';

function groupTitle(statement: Statement): string {
  if (statement.title.trim()) return statement.title;
  const name = statement.institution ?? statement.senderName ?? 'Your bank';
  return `${name} ${ACCOUNT_NOUN[statement.accountType]}${statement.accountMask ? ` ending ${statement.accountMask}` : ''}`;
}

/** Groups alerts by account (institution, type and last digits), keeping the order in which accounts first appear. */
export function groupAlerts(statements: Statement[]): AlertGroup[] {
  const groups = new Map<string, AlertGroup>();
  for (const statement of statements.filter(isAlert)) {
    const key = [(statement.institution ?? statement.senderName ?? '').toLowerCase(), statement.accountType, statement.accountMask ?? ''].join('|');
    const group = groups.get(key);
    if (group) {
      group.alerts.push(statement);
      group.signInUrl ??= statement.signInUrl;
      group.downloadHint ??= statement.downloadHint;
    } else {
      groups.set(key, {
        key,
        title: groupTitle(statement),
        institution: statement.institution ?? statement.senderName,
        signInUrl: statement.signInUrl,
        downloadHint: statement.downloadHint,
        alerts: [statement],
      });
    }
  }
  for (const group of groups.values()) group.alerts.sort((a, b) => alertDate(a).localeCompare(alertDate(b)));
  return [...groups.values()];
}

/** Short month names for a group's alerts, oldest first and without repeats: ["Jul", "Aug", "Sep"]. */
export function alertMonths(group: AlertGroup): { id: string; label: string }[] {
  const seen = new Set<string>();
  const months: { id: string; label: string }[] = [];
  for (const alert of group.alerts) {
    const date = alertDate(alert);
    if (!date) continue;
    const label = formatMonth(date);
    if (seen.has(label)) continue;
    seen.add(label);
    months.push({ id: alert.id, label });
  }
  return months;
}

/** "Statement ready Sep 15" for one alert; "3 statements ready" for several (the months follow, see alertMonths). */
export function alertCountLabel(group: AlertGroup, dateFormat: string): string {
  if (group.alerts.length === 1) {
    const date = group.alerts[0]?.receivedAt ?? group.alerts[0]?.periodEnd;
    return date ? `Statement ready ${formatShortDate(date, dateFormat)}` : 'Statement ready';
  }
  return `${group.alerts.length} statements ready`;
}

/** The whole line, as read aloud: "3 statements ready · Jul, Aug, Sep". Long runs of months read as a range. */
export function alertReadyLabel(group: AlertGroup, dateFormat: string): string {
  const count = alertCountLabel(group, dateFormat);
  if (group.alerts.length === 1) return count;
  const months = alertMonths(group).map((m) => m.label);
  if (months.length === 0) return count;
  return `${count} · ${months.length > 4 ? `${months[0]} – ${months[months.length - 1]}` : months.join(', ')}`;
}

/** What to do, from FinSight's list of banks when it knows this one. */
export function alertGuidance(group: AlertGroup): string {
  const hint = group.downloadHint?.trim() || GENERIC_DOWNLOAD_HINT;
  return group.alerts.length > 1 ? `${hint} ${SEVERAL_PDFS_HINT}` : hint;
}

/** A PDF by type or, when the browser doesn't say (common for drag and drop on Windows), by extension. */
export function isPdfFile(file: File): boolean {
  if (file.type === 'application/pdf' || file.type === 'application/x-pdf') return true;
  return (file.type === '' || file.type === 'application/octet-stream') && /\.pdf$/i.test(file.name);
}

/** What file pickers offer: PDF statements, and CSV, OFX and QFX transaction downloads. */
export const STATEMENT_FILE_ACCEPT = '.pdf,.csv,.ofx,.qfx,application/pdf,text/csv';

export const UNSUPPORTED_FILE_MESSAGE = 'FinSight reads PDF, CSV, OFX and QFX files. Download one of those from your bank.';

/**
 * A file FinSight can read: a PDF, or a CSV, OFX or QFX download. Judged by name, because browsers report these types
 * inconsistently (a CSV is often "application/vnd.ms-excel" on Windows, an OFX file has no type at all). The server
 * checks the content itself.
 */
export function isStatementFile(file: File): boolean {
  return isPdfFile(file) || /\.(pdf|csv|ofx|qfx)$/i.test(file.name);
}

/** Failure codes a password fixes. */
export const PASSWORD_FAILURE_CODES = ['pdf_password_protected', 'pdf_password_incorrect'] as const;

export const needsPassword = (code: string | null | undefined) => (PASSWORD_FAILURE_CODES as readonly (string | null | undefined)[]).includes(code);

/** "PDF", "CSV", "OFX" or "QFX (Quicken)", for a statement's details. */
export function formatLabel(format: Statement['format']): string | null {
  if (!format) return null;
  return format === 'qfx' ? 'QFX (Quicken)' : format.toUpperCase();
}
