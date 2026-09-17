import type { Statement } from '@/api/schemas';
import { isAlert, isAlertOrigin, isInProgress } from '@/lib/statements';

export const STEP_IDS = ['source', 'connect', 'scan', 'confirm', 'add', 'key', 'import', 'finish'] as const;
export type StepId = (typeof STEP_IDS)[number];
export type StepState = 'done' | 'skipped' | 'current' | 'upcoming';

/** How the user gets statements, as answered in the first step. "Not sure" takes the Gmail path and lets the scan tell. */
export type StatementSource = 'email' | 'download' | 'unsure';
export const STATEMENT_SOURCES: readonly StatementSource[] = ['email', 'download', 'unsure'];

/** Which steps show: Gmail (connect, scan, confirm), download (add PDFs), or not chosen yet. */
export type OnboardingPath = 'email' | 'download' | 'undecided';

/** Choices that only the interface knows about, remembered for the tab (see useSessionState). */
export interface OnboardingChoices {
  source: StatementSource | null;
  scanSkipped: boolean;
  /** The Gmail path's confirm step was continued or skipped. */
  confirmed: boolean;
  /** The download path's add step was continued or skipped. */
  added: boolean;
  keySkipped: boolean;
  importSkipped: boolean;
}

export const NO_CHOICES: OnboardingChoices = { source: null, scanSkipped: false, confirmed: false, added: false, keySkipped: false, importSkipped: false };

export interface OnboardingFacts {
  gmailAvailable: boolean;
  /** Connected and not expired. */
  gmailConnected: boolean;
  statements: Statement[];
  /** Files still being sent, before the server knows about them. */
  uploadsInFlight?: boolean;
  hasUserKey: boolean;
  scanning: boolean;
  importing: boolean;
  /** Discovered Gmail statements currently selected for import. */
  selectedCount: number;
  replacingKey: boolean;
  /** The first step was reopened with "Change". */
  choosingSource?: boolean;
  /** The option highlighted on the first step but not yet confirmed; the step list previews its path. */
  previewSource?: StatementSource | null;
  choices: OnboardingChoices;
}

export interface StatementGroups {
  /** Gmail attachments: not alerts, and not alerts the user has since fulfilled with an upload. */
  fromGmail: Statement[];
  discovered: Statement[];
  /** "Statement ready" emails without a PDF. */
  alerts: Statement[];
  /** PDFs the user added: manual uploads, and alerts fulfilled with a PDF. */
  uploads: Statement[];
  processed: Statement[];
  failed: Statement[];
  /** Selected Gmail attachments have been sent for processing. */
  importStarted: boolean;
  /** Anything at all came from Gmail, alerts included. */
  foundInGmail: boolean;
}

export function groupStatements(statements: Statement[]): StatementGroups {
  const fromGmail = statements.filter((s) => s.source === 'gmail' && !isAlertOrigin(s));
  return {
    fromGmail,
    discovered: fromGmail.filter((s) => s.status === 'discovered'),
    alerts: statements.filter(isAlert),
    uploads: statements.filter((s) => !isAlert(s) && (s.source === 'manualUpload' || isAlertOrigin(s))),
    processed: statements.filter((s) => s.status === 'processed'),
    failed: statements.filter((s) => s.status === 'failed'),
    importStarted: fromGmail.some((s) => s.status !== 'discovered'),
    foundInGmail: statements.some((s) => s.source === 'gmail'),
  };
}

/**
 * The answer to "How do you get your statements?": what the user chose, or what the server already shows. Without
 * Gmail on the server there's only one way. A connected inbox or anything found in it means email; uploads with no
 * Gmail connection mean downloading.
 */
export function inferSource(facts: Pick<OnboardingFacts, 'gmailAvailable' | 'gmailConnected' | 'statements' | 'choices'>): StatementSource | null {
  if (!facts.gmailAvailable) return 'download';
  if (facts.choices.source) return facts.choices.source;
  const groups = groupStatements(facts.statements);
  if (facts.gmailConnected || groups.foundInGmail) return 'email';
  if (groups.uploads.length > 0) return 'download';
  return null;
}

export const pathFor = (source: StatementSource | null): OnboardingPath => (source === null ? 'undecided' : source === 'download' ? 'download' : 'email');

/** The steps shown for a path. Before a choice, a single "Add your statements" step stands in for either path. */
export function stepsFor(path: OnboardingPath, gmailAvailable: boolean): StepId[] {
  if (!gmailAvailable) return ['add', 'key', 'import', 'finish'];
  if (path === 'email') return ['source', 'connect', 'scan', 'confirm', 'key', 'import', 'finish'];
  return ['source', 'add', 'key', 'import', 'finish'];
}

export interface DerivedSteps {
  steps: StepId[];
  states: Record<StepId, StepState>;
  current: StepId;
  source: StatementSource | null;
  path: OnboardingPath;
}

/**
 * Works out where the user is from what the server knows (Gmail connection, statements and their status, the saved
 * key, running jobs) plus a few interface choices. The first unfinished step is current, so a refresh or the Google
 * consent round trip lands in the right place. A step after the current one only shows as finished when the server
 * says so; interface choices never mark steps ahead of the user as done.
 */
export function deriveSteps(facts: OnboardingFacts): DerivedSteps {
  const { choices } = facts;
  const groups = groupStatements(facts.statements);
  const source = inferSource(facts);
  const path = facts.gmailAvailable ? pathFor(source) : 'download';
  // While the question is open, list the steps of the highlighted answer; progress still follows the confirmed one.
  const previewing = facts.previewSource && (path === 'undecided' || facts.choosingSource);
  const steps = stepsFor(previewing ? pathFor(facts.previewSource ?? null) : path, facts.gmailAvailable);
  const emailPath = path === 'email';
  const importing = emailPath && facts.importing;
  const scanning = emailPath && facts.scanning;
  const selectedCount = emailPath ? facts.selectedCount : 0;
  const importStarted = emailPath && (importing || groups.importStarted);
  const uploadsBusy = (facts.uploadsInFlight ?? false) || groups.uploads.some(isInProgress);

  // Finished according to the server alone.
  const serverDone: Record<StepId, boolean> = {
    source: false,
    connect: facts.gmailConnected,
    scan: !scanning && groups.foundInGmail,
    confirm: groups.importStarted,
    add: false,
    key: facts.hasUserKey && !facts.replacingKey,
    import: importStarted && !importing && !uploadsBusy,
    finish: false,
  };

  const chosen = emailPath ? choices.confirmed : choices.added;
  const finished: Record<StepId, 'done' | 'skipped' | false> = {
    source: path !== 'undecided' && !facts.choosingSource ? 'done' : false,
    connect: serverDone.connect ? 'done' : false,
    scan: serverDone.scan || (!scanning && choices.scanSkipped) ? 'done' : false,
    confirm: serverDone.confirm || choices.confirmed ? 'done' : false,
    add: choices.added ? 'done' : false,
    key: serverDone.key ? 'done' : choices.keySkipped && !facts.replacingKey ? 'skipped' : false,
    import:
      importing || uploadsBusy
        ? false
        : serverDone.import || choices.importSkipped || (chosen && selectedCount === 0 && !importStarted)
          ? 'done'
          : false,
    finish: false,
  };

  // Progress that is already running takes the focus, wherever it sits in the list; so does reopening the question.
  const current: StepId = facts.choosingSource && steps.includes('source')
    ? 'source'
    : importing
      ? 'import'
      : scanning
        ? 'scan'
        : (steps.find((id) => !finished[id]) ?? 'finish');
  const currentIndex = steps.indexOf(current);

  const states = Object.fromEntries(
    STEP_IDS.map((id): [StepId, StepState] => {
      const index = steps.indexOf(id);
      if (index < 0) return [id, 'upcoming'];
      if (id === current) return [id, 'current'];
      const state = finished[id];
      if (index < currentIndex) return [id, state || 'upcoming'];
      return [id, state && (serverDone[id] || state === 'skipped' || id === 'source') ? state : 'upcoming'];
    }),
  ) as Record<StepId, StepState>;

  return { steps, states, current, source, path };
}
