import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { AnimatePresence, motion } from 'motion/react';
import { FileText, FileUp, Info, Landmark, Loader2, Mail, MailSearch, RefreshCw, SearchX, Sparkles } from 'lucide-react';
import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { useNavigate } from 'react-router';
import { errorMessage } from '@/api/client';
import { api } from '@/api/endpoints';
import { keys, useAiKey, useCompleteOnboarding, useGmail, useSession, useSettings, useUpdateSettings } from '@/api/queries';
import type { AiKey, GmailConnection, Job, JobStep, Statement } from '@/api/schemas';
import { useConfirm } from '@/app/providers/ConfirmProvider';
import { useJobs, type UploadItem, type UploadState } from '@/app/providers/JobsProvider';
import { useToast } from '@/app/providers/ToastProvider';
import { ProgressChecklist } from '@/components/ProgressChecklist';
import { UploadProgressList, usePdfPicker, type UploadRowData } from '@/components/UploadProgressList';
import { AutoHeight, Collapse } from '@/components/ui/AutoHeight';
import { Button, buttonStyles } from '@/components/ui/Button';
import { PopUpButton } from '@/components/ui/PopUpButton';
import { Card, ErrorState, Pill, Skeleton } from '@/components/ui/primitives';
import { RoundCheckbox } from '@/components/ui/RoundCheckbox';
import { GeminiKeyForm } from '@/features/ai/GeminiKeyForm';
import { StatementAlertList, uploadRows } from '@/features/statements/StatementAlerts';
import { useGmailReturn } from '@/hooks/useGmailReturn';
import { usePreferences } from '@/hooks/usePreferences';
import { useSessionState } from '@/hooks/useSessionState';
import { useSingleFlight } from '@/hooks/useSingleFlight';
import { cn } from '@/lib/cn';
import { formatDate } from '@/lib/format';
import { gmailConnectUrl, type GmailOutcome } from '@/lib/gmail';
import { CURRENCY_OPTIONS, DATE_FORMAT_OPTIONS } from '@/lib/preferences';
import { discoveredName, isInProgress, isPreselected, matchCaveat } from '@/lib/statements';
import { AddStatementsStep } from './AddStatementsStep';
import { ONBOARDING_PATH } from './paths';
import { SOURCE_COPY, SourceStep } from './SourceStep';
import { Notice, StepCard } from './StepCard';
import { deriveSteps, groupStatements, NO_CHOICES, type OnboardingChoices, type StatementSource, type StepId } from './steps';

/** How often statements are re-read while uploaded PDFs are still being processed. */
export const UPLOAD_POLL_MS = 1500;

/** PDFs added from the onboarding steps themselves (alert cards keep their own lists). */
export const ONBOARDING_UPLOAD_SCOPE = 'onboarding';

const plural = (count: number, one: string, many = `${one}s`) => `${count} ${count === 1 ? one : many}`;

/** Placeholder steps shown while a sync job is starting, sized and worded like the real ones. */
const SYNC_PLACEHOLDER: JobStep[] = [
  { key: 'gmail', label: 'Connecting to Gmail', status: 'running', detail: null },
  { key: 'search', label: 'Finding statements', status: 'pending', detail: null },
];

const ROW_MOTION = {
  layout: 'position',
  initial: { opacity: 0, y: -4 },
  animate: { opacity: 1, y: 0 },
  exit: { opacity: 0, transition: { duration: 0.15 } },
  transition: { type: 'spring', stiffness: 500, damping: 40 },
} as const;

/** A tinted glyph beside a headline: the calm "working on it" and "nothing here" states of a step. */
function StatusLine({ icon, title, detail }: { icon: ReactNode; title: string; detail?: ReactNode }) {
  return (
    <div className="flex items-center gap-3.5">
      <span className="flex size-11 shrink-0 items-center justify-center rounded-[14px] bg-accent-soft text-accent" aria-hidden="true">
        {icon}
      </span>
      <div className="min-w-0">
        <p className="text-[0.9375rem] font-semibold">{title}</p>
        {detail && <p className="text-[0.875rem] leading-snug text-label-secondary">{detail}</p>}
      </div>
    </div>
  );
}

const Actions = ({ children, className }: { children: ReactNode; className?: string }) => <div className={cn('mt-5 flex flex-col gap-2 *:w-full sm:flex-row sm:flex-wrap sm:items-center sm:*:w-auto', className)}>{children}</div>;
const Lead = ({ children }: { children: ReactNode }) => <p className="text-[0.9375rem] leading-relaxed text-label-secondary">{children}</p>;

/** A statement the user added, as a row in an upload list (for statements added before this visit). */
function statementRow(statement: Statement): UploadRowData {
  const state: UploadState = statement.status === 'processed' ? 'done' : statement.status === 'failed' ? 'failed' : 'processing';
  return {
    key: statement.id,
    name: statement.status === 'processed' || !statement.filename ? statement.title : statement.filename,
    state,
    message: statement.failureMessage ?? 'This statement couldn’t be read.',
    detail: state === 'done' ? plural(statement.transactionCount, 'transaction') : null,
  };
}

const STEP_STATUS: Record<UploadState, JobStep['status']> = { queued: 'pending', uploading: 'running', processing: 'running', done: 'done', failed: 'failed' };

// ---------------------------------------------------------------------------------------------------------------
// Step 1 (email path): Connect Gmail

const OUTCOME_NOTICE: Partial<Record<GmailOutcome, { tone: 'attention' | 'critical'; title: string; text: string }>> = {
  denied: {
    tone: 'attention',
    title: 'Gmail wasn’t connected',
    text: 'FinSight didn’t get permission to read your email. Try again and allow read-only access, or upload your statements as PDFs instead.',
  },
  failed: { tone: 'critical', title: 'Connecting didn’t finish', text: 'Something interrupted the connection with Google. Try again.' },
  unavailable: { tone: 'attention', title: 'Gmail isn’t available', text: 'Gmail integration isn’t set up on this server. Upload your statements as PDFs instead.' },
  demo: { tone: 'attention', title: 'Gmail isn’t available', text: 'This account can’t connect Gmail. Upload your statements as PDFs instead.' },
};

function ConnectStep({ connection, outcome, unsure, onUploadInstead }: { connection?: GmailConnection; outcome: GmailOutcome | null; unsure: boolean; onUploadInstead: () => void }) {
  const [leaving, setLeaving] = useState(false);
  const expired = connection?.connected && connection.status === 'expired';
  const notice = outcome ? OUTCOME_NOTICE[outcome] : undefined;

  // Coming back to this page with the browser's Back button restores it from cache, spinner included.
  useEffect(() => {
    const reset = (event: PageTransitionEvent) => event.persisted && setLeaving(false);
    window.addEventListener('pageshow', reset);
    return () => window.removeEventListener('pageshow', reset);
  }, []);

  return (
    <>
      <Lead>
        {unsure && 'Connect Gmail and FinSight will check whether your bank emails statements. '}
        FinSight asks Google for <span className="text-label">read-only</span> access and opens only emails that look like bank or card statements. PDFs are read once and never
        stored.
      </Lead>
      {(notice || expired) && (
        <div className="mt-4">
          {notice ? (
            <Notice tone={notice.tone} title={notice.title} role="alert">
              {notice.text}
            </Notice>
          ) : (
            <Notice tone="attention" title="Your Gmail connection expired">
              Reconnect {connection?.email ?? 'your account'} so FinSight can look for statements.
            </Notice>
          )}
        </div>
      )}
      <Actions>
        <a
          href={gmailConnectUrl(ONBOARDING_PATH)}
          onClick={(event) => {
            if (leaving) event.preventDefault();
            else setLeaving(true);
          }}
          aria-disabled={leaving || undefined}
          className={buttonStyles({ className: cn(leaving && 'pointer-events-none') })}
        >
          {leaving ? <Loader2 size={16} className="animate-spin" aria-hidden="true" /> : <Mail size={16} aria-hidden="true" />}
          {expired ? 'Reconnect Gmail' : outcome && outcome !== 'connected' ? 'Try again' : 'Connect Gmail'}
        </a>
        <Button variant="secondary" icon={<FileUp size={16} aria-hidden="true" />} onClick={onUploadInstead}>
          Upload PDFs instead
        </Button>
      </Actions>
    </>
  );
}

// ---------------------------------------------------------------------------------------------------------------
// Step 2 (email path): Scan

type ScanView = { kind: 'idle' } | { kind: 'scanning'; steps: JobStep[] } | { kind: 'failed'; message: string } | { kind: 'empty' };

function ScanStep({ view, email, unsure, onScan, onDownloadInstead, onSkip }: { view: ScanView; email: string | null; unsure: boolean; onScan: () => void; onDownloadInstead: () => void; onSkip: () => void }) {
  const inbox = email ? <span className="text-label">{email}</span> : 'your inbox';
  return (
    <AutoHeight>
      {view.kind === 'scanning' && (
        <div className="fade-in">
          <StatusLine icon={<MailSearch size={22} />} title="Looking through your inbox…" detail="This usually takes less than a minute." />
          <div className="mt-5 rounded-[18px] bg-fill/60 px-4 py-4">
            <ProgressChecklist steps={view.steps} />
          </div>
        </div>
      )}
      {view.kind === 'idle' && (
        <div className="fade-in">
          <Lead>
            {unsure ? (
              <>FinSight will look through {inbox} for statement emails from banks and card issuers, and tell you what it finds.</>
            ) : (
              <>FinSight will look through {inbox} for statements from banks and card issuers, including emails that only say a statement is ready.</>
            )}
          </Lead>
          <Actions>
            <Button icon={<MailSearch size={16} aria-hidden="true" />} onClick={onScan}>
              Scan my inbox
            </Button>
          </Actions>
        </div>
      )}
      {view.kind === 'failed' && (
        <div className="fade-in">
          <Notice tone="critical" title="The scan didn’t finish" role="alert">
            {view.message}
          </Notice>
          <Actions>
            <Button icon={<RefreshCw size={16} aria-hidden="true" />} onClick={onScan}>
              Try again
            </Button>
            <Button variant="secondary" icon={<FileUp size={16} aria-hidden="true" />} onClick={onDownloadInstead}>
              Upload PDFs instead
            </Button>
          </Actions>
        </div>
      )}
      {view.kind === 'empty' && (
        <div className="fade-in">
          <StatusLine
            icon={<SearchX size={22} />}
            title="No statements found"
            detail={unsure ? 'Your bank probably doesn’t email statements. You can download them from its website instead.' : 'FinSight looks for statement emails from banks and card issuers.'}
          />
          {!unsure && (
            <ul className="mt-4 list-disc space-y-1 pl-5 text-[0.875rem] leading-snug text-label-secondary marker:text-label-tertiary">
              <li>Check that your statement emails arrive in this Gmail account.</li>
              <li>If your bank doesn’t email them, download PDFs from its website and add them instead.</li>
            </ul>
          )}
          <Actions>
            <Button variant={unsure ? 'primary' : 'secondary'} icon={<Landmark size={16} aria-hidden="true" />} onClick={onDownloadInstead}>
              Download from my bank instead
            </Button>
            <Button variant="secondary" icon={<RefreshCw size={16} aria-hidden="true" />} onClick={onScan}>
              Scan again
            </Button>
            <Button variant="plain" onClick={onSkip}>
              Continue without statements
            </Button>
          </Actions>
        </div>
      )}
    </AutoHeight>
  );
}

// ---------------------------------------------------------------------------------------------------------------
// Step 3 (email path): Confirm

function DiscoveredRow({ statement, selected, onToggle, dateFormat }: { statement: Statement; selected: boolean; onToggle: () => void; dateFormat: string }) {
  const caveat = matchCaveat(statement);
  const reasons = statement.detectionReasons.filter((r) => !/looks like a (pay stub|receipt)/i.test(r));
  const meta = [statement.receivedAt ? formatDate(statement.receivedAt, dateFormat) : null, statement.filename].filter(Boolean).join(' · ');
  return (
    <motion.li {...ROW_MOTION}>
      <label className="flex cursor-pointer items-start gap-3.5 px-4 py-3.5 transition-colors hover:bg-fill md:px-5">
        <span className="flex h-10 items-center">
          <RoundCheckbox checked={selected} onChange={onToggle} />
        </span>
        <span className="hidden size-10 shrink-0 items-center justify-center rounded-xl bg-fill text-label-secondary sm:flex" aria-hidden="true">
          <FileText size={19} />
        </span>
        <span className="min-w-0 flex-1">
          <span className="flex min-w-0 items-center gap-2">
            <span className="truncate text-[0.9375rem] font-medium">{discoveredName(statement)}</span>
            {caveat && <Pill>{caveat}</Pill>}
          </span>
          <span className="block truncate text-[0.875rem] text-label-secondary">{statement.subject ?? statement.title}</span>
          <span className="caption block truncate text-label-tertiary">{meta}</span>
          {reasons.length > 0 && (
            <span className="mt-1 flex items-center gap-1.5 text-[0.8125rem] leading-snug text-label-secondary">
              <Info size={13} className="shrink-0 text-label-tertiary" aria-hidden="true" />
              <span className="min-w-0 truncate">
                <span className="sr-only">Why FinSight suggested it: </span>
                {reasons.slice(0, 2).join(' · ')}
              </span>
            </span>
          )}
        </span>
      </label>
    </motion.li>
  );
}

interface ConfirmStepProps {
  discovered: Statement[];
  /** Every statement; the alert cards pick out and group the alerts. */
  statements: Statement[];
  alertCount: number;
  /** Alert cards are still showing upload progress, even if their last alert has been fulfilled. */
  alertUploadsActive: boolean;
  addedRows: UploadRowData[];
  selected: Set<string>;
  onToggle: (id: string) => void;
  onSelectAll: (all: boolean) => void;
  onUpload: () => void;
  /** At least one PDF has been added (from an alert card or by hand). */
  uploadsStarted: boolean;
  onContinue: () => void;
  onSkip: () => void;
}

function ConfirmStep({ discovered, statements, alertCount, alertUploadsActive, addedRows, selected, onToggle, onSelectAll, onUpload, uploadsStarted, onContinue, onSkip }: ConfirmStepProps) {
  const prefs = usePreferences();
  const allSelected = discovered.length > 0 && selected.size === discovered.length;
  const canContinue = selected.size > 0 || uploadsStarted;
  const showAlerts = alertCount > 0 || alertUploadsActive;

  return (
    <>
      {discovered.length > 0 ? (
        <>
          <Lead>These emails look like statements. Keep the ones that are yours; anything you uncheck is left alone.</Lead>
          <div className="mt-4 mb-2 flex items-center justify-between gap-3 px-1">
            <p className="caption tabular" aria-live="polite">
              {selected.size} of {discovered.length} selected
            </p>
            <Button variant="plain" size="sm" className="-mr-2" onClick={() => onSelectAll(!allSelected)}>
              {allSelected ? 'Select none' : 'Select all'}
            </Button>
          </div>
          <ul aria-label="Possible statements" className="card grouped overflow-hidden rounded-[20px]">
            <AnimatePresence initial={false}>
              {discovered.map((s) => (
                <DiscoveredRow key={s.id} statement={s} selected={selected.has(s.id)} onToggle={() => onToggle(s.id)} dateFormat={prefs.dateFormat} />
              ))}
            </AnimatePresence>
          </ul>
        </>
      ) : alertCount > 0 ? (
        <Lead>Your bank emailed that these statements are ready but didn’t attach them. Download each one from online banking, then upload it here.</Lead>
      ) : (
        <Lead>Download PDF statements from your bank’s website and add them here. FinSight reads each one as soon as it’s added.</Lead>
      )}

      <Collapse open={showAlerts}>
        <section aria-labelledby="alerts-heading" className={cn(discovered.length > 0 ? 'mt-6' : 'mt-4')}>
          <h3 id="alerts-heading" className={cn('eyebrow px-1', discovered.length === 0 && 'sr-only')}>
            Statements to download
          </h3>
          {discovered.length > 0 && <p className="caption mt-0.5 mb-2.5 px-1">These emails say a statement is ready but didn’t include the PDF.</p>}
          <StatementAlertList statements={statements} dateFormat={prefs.dateFormat} />
        </section>
      </Collapse>

      <Collapse open={addedRows.length > 0}>
        <h3 className="eyebrow mt-5 mb-2 px-1">Added by you</h3>
        <UploadProgressList label="Added by you" rows={addedRows} />
      </Collapse>

      <div className="mt-3">
        <Button variant={discovered.length > 0 || alertCount > 0 ? 'plain' : 'secondary'} className={cn((discovered.length > 0 || alertCount > 0) && '-ml-2')} icon={<FileUp size={16} aria-hidden="true" />} onClick={onUpload}>
          {discovered.length > 0 || alertCount > 0 ? 'Add a PDF yourself' : addedRows.length > 0 ? 'Add more PDFs' : 'Choose PDF files'}
        </Button>
      </div>

      <Actions className="mt-6">
        <Button disabled={!canContinue} onClick={onContinue}>
          {selected.size > 0 ? `Continue with ${plural(selected.size, 'statement')}` : 'Continue'}
        </Button>
        <Button variant="plain" onClick={onSkip}>
          Skip for now
        </Button>
      </Actions>
    </>
  );
}

// ---------------------------------------------------------------------------------------------------------------
// Gemini key

function KeyStep({ aiKey, replacing, onSaved, onSkip, onCancelReplace }: { aiKey?: AiKey; replacing: boolean; onSaved: (key: AiKey) => void; onSkip: () => void; onCancelReplace: () => void }) {
  const builtIn = aiKey?.serverKeyAvailable ?? false;
  return (
    <>
      <Lead>Gemini sorts your merchants into categories and writes plain-language insights about your spending. The numbers themselves are always calculated by FinSight, never by AI.</Lead>
      {!replacing && (
        <div className="mt-4">
          <Notice>{builtIn ? 'This step is optional. If you skip it, FinSight’s built-in AI is used.' : 'Without a key, categories come from FinSight’s built-in rules and AI insights stay off. You can add one later in Settings.'}</Notice>
        </div>
      )}
      <div className="mt-5">
        <GeminiKeyForm
          onSaved={onSaved}
          onCancel={replacing ? onCancelReplace : undefined}
          secondaryAction={
            replacing ? undefined : (
              <Button variant="plain" onClick={onSkip}>
                {builtIn ? 'Skip' : 'Skip for now'}
              </Button>
            )
          }
        />
      </div>
    </>
  );
}

// ---------------------------------------------------------------------------------------------------------------
// Import

type ImportView = { kind: 'idle'; count: number; error: string | null } | { kind: 'running'; steps: JobStep[] | null; count: number };

function ImportStep({ view, onStart, onSkip }: { view: ImportView; onStart: () => void; onSkip: () => void }) {
  return (
    <AutoHeight>
      {view.kind === 'running' ? (
        <div className="fade-in">
          <StatusLine icon={<Sparkles size={22} />} title="Reading your statements…" detail="Transactions are extracted and categorized. This takes a minute or two." />
          <div className="mt-5 rounded-[18px] bg-fill/60 px-4 py-4">
            {view.steps ? (
              <ProgressChecklist steps={view.steps} />
            ) : (
              <div className="space-y-3" aria-hidden="true">
                {Array.from({ length: Math.max(view.count, 1) + 2 }, (_, i) => (
                  <div key={i} className="flex h-5 items-center gap-3">
                    <Skeleton className="size-5 rounded-full" />
                    <Skeleton className="h-3.5 w-48 max-w-[70%]" />
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>
      ) : (
        <div className="fade-in">
          {view.error && (
            <div className="mb-4">
              <Notice tone="critical" title="The import didn’t finish" role="alert">
                {view.error}
              </Notice>
            </div>
          )}
          <Lead>FinSight is ready to read {plural(view.count, 'statement')} and categorize every transaction.</Lead>
          <Actions>
            <Button onClick={onStart}>{view.error ? 'Try again' : `Import ${plural(view.count, 'statement')}`}</Button>
            <Button variant="plain" onClick={onSkip}>
              Skip for now
            </Button>
          </Actions>
        </div>
      )}
    </AutoHeight>
  );
}

// ---------------------------------------------------------------------------------------------------------------
// Finish

function Figure({ label, value, detail }: { label: string; value: string; detail?: string }) {
  return (
    <div className="rounded-[18px] bg-fill/60 px-4 py-3.5">
      <dt className="caption">{label}</dt>
      <dd className="figure tabular mt-1 text-[1.5rem]">{value}</dd>
      {detail && <dd className="caption mt-0.5 truncate">{detail}</dd>}
    </div>
  );
}

function FinishStep({ processed, failed, waiting, aiOn, aiDetail, finishing, onFinish }: { processed: Statement[]; failed: Statement[]; waiting: number; aiOn: boolean; aiDetail: string; finishing: boolean; onFinish: () => void }) {
  const settings = useSettings();
  const update = useUpdateSettings();
  const currencyLabel = useId();
  const dateFormatLabel = useId();
  const s = settings.data;
  const transactions = processed.reduce((sum, statement) => sum + statement.transactionCount, 0);

  return (
    <>
      <Lead>{processed.length > 0 ? 'Your overview is ready. Here’s what FinSight set up.' : 'You can add statements anytime from the Statements page.'}</Lead>
      <dl className="mt-5 grid grid-cols-3 gap-2">
        <Figure label="Statements" value={String(processed.length)} />
        <Figure label="Transactions" value={transactions.toLocaleString()} />
        <Figure label="AI" value={aiOn ? 'On' : 'Off'} detail={aiDetail} />
      </dl>

      {waiting > 0 && (
        <div className="mt-5">
          <Notice title={waiting === 1 ? 'One statement is still waiting to be downloaded' : `${waiting} statements are still waiting to be downloaded`}>
            You’ll find {waiting === 1 ? 'it' : 'them'} on the Statements page, with a link to your bank.
          </Notice>
        </div>
      )}

      {failed.length > 0 && (
        <section aria-labelledby="failed-title" className="mt-5">
          <h3 id="failed-title" className="eyebrow mb-2 px-1">
            {failed.length === 1 ? 'One statement couldn’t be read' : `${failed.length} statements couldn’t be read`}
          </h3>
          <ul className="card grouped overflow-hidden rounded-[20px]">
            {failed.map((statement) => (
              <li key={statement.id} className="px-4 py-3 md:px-5">
                <p className="truncate text-[0.9375rem]">{statement.source === 'gmail' && !statement.reprocessNeedsUpload ? discoveredName(statement) : statement.filename || statement.title}</p>
                <p className="text-[0.8125rem] text-critical">{statement.failureMessage ?? 'This statement couldn’t be read.'}</p>
              </li>
            ))}
          </ul>
          <p className="caption mt-2 px-1">You can try these again from the Statements page.</p>
        </section>
      )}

      <section aria-label="Preferences" className="mt-5">
        <div className="card grouped overflow-hidden rounded-[20px]">
          <div className="flex min-h-13 items-center justify-between gap-4 px-4 py-2.5 md:px-5">
            <span id={currencyLabel} className="text-[0.9375rem]">
              Currency
            </span>
            {s ? <PopUpButton labelledBy={currencyLabel} value={s.currency} onChange={(currency) => update.mutate({ ...s, currency })} options={CURRENCY_OPTIONS} /> : <Skeleton className="h-9 w-24 rounded-full" />}
          </div>
          <div className="flex min-h-13 items-center justify-between gap-4 px-4 py-2.5 md:px-5">
            <span id={dateFormatLabel} className="text-[0.9375rem]">
              Date format
            </span>
            {s ? <PopUpButton labelledBy={dateFormatLabel} value={s.dateFormat} onChange={(dateFormat) => update.mutate({ ...s, dateFormat })} options={DATE_FORMAT_OPTIONS} /> : <Skeleton className="h-9 w-32 rounded-full" />}
          </div>
        </div>
        {update.isError && (
          <p role="alert" className="fade-in mt-2 px-1 text-[0.875rem] text-critical">
            {errorMessage(update.error)}
          </p>
        )}
      </section>

      <Actions className="mt-6">
        <Button size="lg" loading={finishing} onClick={onFinish}>
          Go to Overview
        </Button>
      </Actions>
    </>
  );
}

// ---------------------------------------------------------------------------------------------------------------

const STEP_COPY: Record<StepId, { title: string; description: string }> = {
  source: { title: 'How do you get your statements?', description: 'So FinSight can set up the right way' },
  connect: { title: 'Connect Gmail', description: 'Let FinSight find statements in your inbox' },
  scan: { title: 'Scan your inbox', description: 'Look for bank and card statements' },
  confirm: { title: 'Confirm your statements', description: 'Choose which ones to import' },
  add: { title: 'Add your statements', description: 'Download PDFs from your bank and add them here' },
  key: { title: 'Add your Gemini API key', description: 'Turn on AI categories and insights' },
  import: { title: 'Import and analyze', description: 'Read every transaction' },
  finish: { title: 'You’re all set', description: 'Review and start exploring' },
};

function scanSummary(statements: number, alerts: number): string {
  if (statements > 0 && alerts > 0) return `Found ${plural(statements, 'statement')} and ${plural(alerts, 'alert')}`;
  if (statements > 0) return `Found ${plural(statements, 'statement')}`;
  if (alerts > 0) return `Found ${plural(alerts, 'statement alert')}`;
  return 'No statements found';
}

/**
 * First-run setup, as one page in the manner of Apple's Setup Assistant: every step is visible, the current one is
 * open, finished ones fold down to a check and a summary. It starts by asking how statements arrive, and shows the
 * Gmail steps or the download step to match. Where the user is comes from the server each time (see deriveSteps), so
 * a refresh or the round trip through Google's consent screen picks up at the same place.
 */
export default function OnboardingPage() {
  const session = useSession();
  const user = session.data?.user;
  const gmailAvailable = session.data?.capabilities.gmail ?? false;
  const gmail = useGmail(gmailAvailable);
  const aiKey = useAiKey();
  const statements = useQuery({
    queryKey: keys.statements,
    queryFn: api.statements,
    // Uploaded PDFs are processed in the background; keep their status current until they finish.
    refetchInterval: (query) => (query.state.data?.some(isInProgress) ? UPLOAD_POLL_MS : false),
  });
  const jobs = useJobs();
  const client = useQueryClient();
  const confirm = useConfirm();
  const toast = useToast();
  const navigate = useNavigate();
  const complete = useCompleteOnboarding();
  const once = useSingleFlight();

  const storageKey = `finsight.onboarding.${user?.id ?? 'anonymous'}`;
  const [storedChoices, setChoices] = useSessionState<OnboardingChoices>(`${storageKey}.choices`, NO_CHOICES);
  // Stored choices from an earlier version of this page may lack newer fields.
  const choices: OnboardingChoices = { ...NO_CHOICES, ...storedChoices };
  const [selection, setSelection] = useSessionState<string[] | null>(`${storageKey}.selection`, null);
  const [replacingKey, setReplacingKey] = useState(false);
  const [choosingSource, setChoosingSource] = useState(false);
  const [previewSource, setPreviewSource] = useState<StatementSource | null>(null);
  const [outcome, setOutcome] = useState<GmailOutcome | null>(null);
  const [startedKind, setStartedKind] = useState<Job['kind'] | null>(null);
  const choose = (patch: Partial<OnboardingChoices>) => setChoices({ ...choices, ...patch });

  const trackAs = (kind: Job['kind']) => ({ jobId }: { jobId: string }) => {
    setStartedKind(kind);
    jobs.track(jobId);
  };
  const sync = useMutation({ mutationFn: api.syncStatements, onSuccess: trackAs('sync') });
  const process = useMutation({ mutationFn: (ids: string[]) => api.processStatements(ids), onSuccess: trackAs('process') });
  const picker = usePdfPicker((files) => void jobs.uploadFiles(files, { scope: ONBOARDING_UPLOAD_SCOPE }));

  // The job in flight, including the moment between the request being accepted and its first progress arriving.
  const job = jobs.job;
  const busyKind: Job['kind'] | null = sync.isPending
    ? 'sync'
    : process.isPending
      ? 'process'
      : jobs.isLoading
        ? startedKind
        : job && (jobs.isActive || jobs.isRefreshing)
          ? job.kind
          : null;

  const all = statements.data ?? [];
  const groups = groupStatements(all);
  const discoveredIds = new Set(groups.discovered.map((s) => s.id));
  const selected = new Set((selection ?? groups.discovered.filter(isPreselected).map((s) => s.id)).filter((id) => discoveredIds.has(id)));
  const importing = busyKind === 'process' || groups.fromGmail.some(isInProgress);
  const scanning = busyKind === 'sync';
  const connection = gmail.data;
  const gmailConnected = outcome === 'connected' || (connection?.connected === true && connection.status !== 'expired');

  // Uploads: files added this visit (tracked with live progress) and, for a resumed visit, statements added before.
  const byId = new Map(all.map((s) => [s.id, s]));
  const trackedIds = new Set(jobs.uploads.flatMap((item) => (item.statementId ? [item.statementId] : [])));
  const doneDetail = (item: UploadItem) => {
    const statement = item.statementId ? byId.get(item.statementId) : undefined;
    return statement?.status === 'processed' ? plural(statement.transactionCount, 'transaction') : null;
  };
  // While a file is being sent, a statement refetch can show it before its upload response names it; leave in-progress
  // statements out until then so the same file never shows twice.
  const sending = jobs.uploads.some((item) => item.state === 'uploading');
  const untrackedUploads = groups.uploads.filter((s) => !trackedIds.has(s.id) && !(sending && isInProgress(s))).map(statementRow);
  const ownUploads = jobs.uploads.filter((item) => item.scope === ONBOARDING_UPLOAD_SCOPE);
  const addedRows = [...uploadRows(ownUploads, (id) => jobs.clearUploads(ONBOARDING_UPLOAD_SCOPE, id), doneDetail), ...untrackedUploads];
  const uploadsInFlight = jobs.uploads.some((item) => item.state === 'queued' || item.state === 'uploading' || item.state === 'processing');
  const liveUploads = jobs.uploads.filter((item) => item.state !== 'failed' || item.jobId !== null);
  const uploadsStarted = groups.uploads.length > 0 || liveUploads.some((item) => item.state !== 'failed');
  const addedCount = Math.max(groups.uploads.length, liveUploads.filter((item) => item.state !== 'failed').length);

  const { steps, states, current, source, path } = deriveSteps({
    gmailAvailable,
    gmailConnected,
    statements: all,
    uploadsInFlight,
    hasUserKey: aiKey.data?.hasUserKey ?? false,
    scanning,
    importing,
    selectedCount: selected.size,
    replacingKey,
    choosingSource,
    previewSource,
    choices,
  });

  const loaded = !statements.isPending && (!gmailAvailable || !gmail.isPending) && !aiKey.isPending && jobs.ready;

  function startScan() {
    choose({ scanSkipped: false, confirmed: false });
    setSelection(null);
    void once(() => sync.mutateAsync());
  }

  function startImport() {
    const ids = [...selected];
    if (ids.length > 0 && !groups.importStarted) void once(() => process.mutateAsync(ids));
  }

  /** After the key step: go straight on to importing what the user chose. */
  function afterKeyStep(patch: Partial<OnboardingChoices> = {}) {
    choose(patch);
    setReplacingKey(false);
    if (path === 'email' && states.confirm === 'done' && !choices.importSkipped) startImport();
  }

  useGmailReturn((result) => {
    setOutcome(result);
    if (result === 'connected') {
      if (choices.source !== 'email' && choices.source !== 'unsure') choose({ source: 'email' });
      void client.invalidateQueries({ queryKey: keys.gmail });
      void once(() => sync.mutateAsync());
    }
  });

  async function finish(askFirst: boolean) {
    await once(async () => {
      if (askFirst && groups.processed.length === 0) {
        const ok = await confirm({
          title: 'Set up later?',
          message: 'You can connect Gmail, add statements and your API key anytime from Statements and Settings.',
          confirmLabel: 'Set up later',
        });
        if (!ok) return;
      }
      try {
        await complete.mutateAsync();
      } catch (error) {
        toast(errorMessage(error), 'error');
        return;
      }
      jobs.dismiss();
      navigate('/', { replace: true });
    });
  }

  // When a step finishes, bring the next one into view and move focus to its heading, so keyboard and screen reader
  // users continue from there instead of from a button that just folded away.
  const previous = useRef<StepId | null>(null);
  useEffect(() => {
    if (!loaded) return;
    if (previous.current === null || previous.current === current) {
      previous.current = current;
      return;
    }
    previous.current = current;
    const heading = document.getElementById(`step-${current}-title`);
    heading?.focus({ preventScroll: true });
    const timer = window.setTimeout(() => {
      const card = heading?.closest('li');
      if (!card) return;
      const top = card.getBoundingClientRect().top;
      if (top < 72 || top > window.innerHeight * 0.55) window.scrollBy({ top: top - 88, behavior: 'smooth' });
    }, 320);
    return () => window.clearTimeout(timer);
  }, [current, loaded]);

  // What each step shows.
  const syncJob = job?.kind === 'sync' ? job : null;
  const scanView: ScanView = scanning
    ? { kind: 'scanning', steps: syncJob?.steps.length ? syncJob.steps : SYNC_PLACEHOLDER }
    : sync.isError || syncJob?.status === 'failed'
      ? { kind: 'failed', message: sync.isError ? errorMessage(sync.error) : (syncJob?.errorMessage ?? 'Try again in a moment.') }
      : connection?.lastSyncedAt || syncJob?.status === 'succeeded'
        ? { kind: 'empty' }
        : { kind: 'idle' };

  // Uploads (from alert cards, the add step or "Add a PDF yourself") count as importing alongside selected statements.
  const uploadSteps: JobStep[] = [...uploadRows(liveUploads, () => undefined, doneDetail), ...untrackedUploads].map((row) => ({
    key: `upload:${row.key}`,
    label: row.name,
    status: STEP_STATUS[row.state],
    detail: row.state === 'failed' ? (row.message ?? null) : row.state === 'done' ? (row.detail ?? null) : null,
  }));
  const uploadsBusy = uploadsInFlight || groups.uploads.some(isInProgress);
  const processJob = job?.kind === 'process' ? job : null;
  const importView: ImportView = importing
    ? { kind: 'running', steps: processJob?.steps ? [...processJob.steps, ...(uploadsBusy ? uploadSteps : [])] : null, count: selected.size }
    : uploadsBusy
      ? { kind: 'running', count: uploadSteps.length, steps: uploadSteps }
      : { kind: 'idle', count: selected.size, error: process.isError ? errorMessage(process.error) : processJob?.status === 'failed' ? (processJob.errorMessage ?? 'Try again in a moment.') : null };

  const key = aiKey.data;
  const aiOn = (key?.hasUserKey ?? false) || (key?.serverKeyAvailable ?? session.data?.capabilities.ai ?? false);
  const aiDetail = key?.hasUserKey ? 'Your key' : aiOn ? 'Built-in' : 'No key';
  const transactions = groups.processed.reduce((sum, s) => sum + s.transactionCount, 0);
  const selectedSummary = groups.importStarted || selected.size > 0 ? `${plural(groups.importStarted ? groups.fromGmail.filter((s) => s.status !== 'discovered').length : selected.size, 'statement')} selected` : null;
  const addedSummary = addedCount > 0 ? `${plural(addedCount, 'PDF')} added` : null;

  const summaries: Record<StepId, ReactNode> = {
    source: source ? SOURCE_COPY[source].summary : null,
    connect: `Connected${connection?.email ? ` as ${connection.email}` : ''}`,
    scan: choices.scanSkipped && !groups.foundInGmail ? 'Skipped' : scanSummary(groups.fromGmail.length, groups.alerts.length),
    confirm: [selectedSummary, addedSummary].filter(Boolean).join(' · ') || 'Skipped for now',
    add: addedSummary ?? 'Skipped for now',
    key: key?.hasUserKey ? `Key saved${key.hint ? ` (${key.hint})` : ''}` : key?.serverKeyAvailable ? 'Skipped. Using FinSight’s built-in AI' : 'Skipped. AI features are off',
    import:
      groups.processed.length > 0 || groups.failed.length > 0
        ? `${plural(groups.processed.length, 'statement')} imported · ${plural(transactions, 'transaction')}`
        : 'Nothing imported yet',
    finish: null,
  };

  const importLocked = importing || groups.importStarted;
  const revisit = (label: string, onClick: () => void) => (
    <Button variant="plain" size="sm" className="-mr-2" onClick={onClick}>
      {label}
    </Button>
  );
  const actions: Partial<Record<StepId, ReactNode>> = {
    source: !importing ? revisit('Change', () => setChoosingSource(true)) : undefined,
    scan: states.scan === 'done' && !importLocked ? revisit('Scan again', startScan) : undefined,
    confirm: !importLocked ? revisit('Change', () => choose({ confirmed: false, importSkipped: false })) : undefined,
    add: revisit('Add more', () => choose({ added: false, importSkipped: false })),
    key: importing ? undefined : key?.hasUserKey ? revisit('Replace', () => setReplacingKey(true)) : revisit('Add key', () => choose({ keySkipped: false })),
  };

  const switchToDownload = () => choose({ source: 'download' });

  const bodies: Record<StepId, ReactNode> = {
    source: (
      <SourceStep
        key={choosingSource ? 'changing' : 'first'}
        value={source}
        onSelect={setPreviewSource}
        onChoose={(next) => {
          choose({ source: next });
          setChoosingSource(false);
          setPreviewSource(null);
        }}
        onCancel={
          choosingSource
            ? () => {
                setChoosingSource(false);
                setPreviewSource(null);
              }
            : undefined
        }
      />
    ),
    connect: <ConnectStep connection={connection} outcome={outcome} unsure={source === 'unsure'} onUploadInstead={switchToDownload} />,
    scan: <ScanStep view={scanView} email={connection?.email ?? null} unsure={source === 'unsure'} onScan={startScan} onDownloadInstead={switchToDownload} onSkip={() => choose({ scanSkipped: true })} />,
    confirm: (
      <ConfirmStep
        discovered={groups.discovered}
        statements={all}
        alertCount={groups.alerts.length}
        alertUploadsActive={jobs.uploads.some((item) => item.scope.startsWith('alert:'))}
        addedRows={addedRows}
        selected={selected}
        onToggle={(id) => {
          const next = new Set(selected);
          if (next.has(id)) next.delete(id);
          else next.add(id);
          setSelection([...next]);
        }}
        onSelectAll={(everything) => setSelection(everything ? [...discoveredIds] : [])}
        onUpload={picker.open}
        uploadsStarted={uploadsStarted}
        onContinue={() => {
          choose({ confirmed: true, importSkipped: false });
          if (states.key === 'done' || states.key === 'skipped') startImport();
        }}
        onSkip={() => {
          setSelection([]);
          choose({ confirmed: true });
        }}
      />
    ),
    add: (
      <AddStatementsStep
        rows={addedRows}
        onFiles={(files) => void jobs.uploadFiles(files, { scope: ONBOARDING_UPLOAD_SCOPE })}
        canContinue={uploadsStarted}
        onContinue={() => choose({ added: true, importSkipped: false })}
        onSkip={() => choose({ added: true })}
      />
    ),
    key: <KeyStep aiKey={key} replacing={replacingKey} onSaved={() => afterKeyStep()} onSkip={() => afterKeyStep({ keySkipped: true })} onCancelReplace={() => setReplacingKey(false)} />,
    import: <ImportStep view={importView} onStart={startImport} onSkip={() => choose({ importSkipped: true })} />,
    finish: (
      <FinishStep
        processed={groups.processed}
        failed={groups.failed}
        waiting={groups.alerts.length}
        aiOn={aiOn}
        aiDetail={aiDetail}
        finishing={complete.isPending}
        onFinish={() => void finish(false)}
      />
    ),
  };

  const currentNumber = steps.indexOf(current) + 1;

  return (
    <div className="min-h-dvh px-4 pt-[max(0.75rem,env(safe-area-inset-top))] pb-[max(3rem,env(safe-area-inset-bottom))] sm:px-6">
      {picker.input}

      <div className="mx-auto max-w-[680px]">
        <div className="flex h-12 items-center justify-end">
          <Button variant="plain" size="sm" className="-mr-2" loading={complete.isPending && current !== 'finish'} onClick={() => void finish(true)}>
            Set up later
          </Button>
        </div>

        <motion.header initial={{ opacity: 0, y: 8 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.45, ease: [0.2, 0.8, 0.2, 1] }} className="mt-2 mb-8 text-center sm:mt-6 sm:mb-10">
          <img src="/favicon.svg" alt="" width={60} height={60} className="mx-auto mb-5 size-[60px] rounded-[17px] shadow-float" />
          <h1 className="title-large">Set up FinSight</h1>
          <p className="mx-auto mt-2 max-w-lg text-[1.0625rem] leading-snug text-balance text-label-secondary">
            {user?.name ? `Welcome, ${user.name.split(' ')[0]}. ` : ''}A few quick steps from your statements to a clear picture of your money.
          </p>
        </motion.header>

        <p className="sr-only" aria-live="polite">
          {loaded ? `Step ${currentNumber} of ${steps.length}: ${STEP_COPY[current].title}` : ''}
        </p>

        {statements.isError ? (
          <Card>
            <ErrorState message={errorMessage(statements.error)} onRetry={() => void statements.refetch()} />
          </Card>
        ) : (
          // Steps join and leave as the path changes; each folds by height with the gap below it (see StepCard).
          <ol className="-mb-3" aria-label="Setup steps" aria-busy={!loaded}>
            <AnimatePresence initial={false}>
              {steps.map((id, index) => {
                const state = loaded ? states[id] : 'upcoming';
                return (
                  <StepCard
                    key={id}
                    id={`step-${id}`}
                    number={index + 1}
                    title={id === 'finish' && state !== 'current' ? 'Finish' : STEP_COPY[id].title}
                    description={STEP_COPY[id].description}
                    summary={summaries[id]}
                    state={state}
                    action={actions[id]}
                  >
                    {bodies[id]}
                  </StepCard>
                );
              })}
            </AnimatePresence>
          </ol>
        )}
      </div>
    </div>
  );
}
