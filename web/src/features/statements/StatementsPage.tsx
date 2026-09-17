import { useMutation, useQueryClient } from '@tanstack/react-query';
import { AnimatePresence, motion } from 'motion/react';
import { ChevronRight, FileText, FileUp, Mail, RefreshCw } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { useSearchParams } from 'react-router';
import { errorMessage } from '@/api/client';
import { api } from '@/api/endpoints';
import { keys, useGmail, useSession, useStatements } from '@/api/queries';
import type { Statement } from '@/api/schemas';
import { useJobs } from '@/app/providers/JobsProvider';
import { useToast } from '@/app/providers/ToastProvider';
import { PageHeader } from '@/components/PageHeader';
import { UploadProgressList, usePdfPicker } from '@/components/UploadProgressList';
import { Collapse } from '@/components/ui/AutoHeight';
import { Button, buttonStyles } from '@/components/ui/Button';
import { Card, EmptyState, ErrorState, GroupedList, Skeleton } from '@/components/ui/primitives';
import { RoundCheckbox } from '@/components/ui/RoundCheckbox';
import { Tooltip, TruncatedText } from '@/components/ui/Tooltip';
import { useGmailReturn } from '@/hooks/useGmailReturn';
import { usePreferences } from '@/hooks/usePreferences';
import { useSingleFlight } from '@/hooks/useSingleFlight';
import { accountLabel, formatDate, formatRelativeTime } from '@/lib/format';
import { GMAIL_OUTCOME_MESSAGES, gmailConnectUrl } from '@/lib/gmail';
import { discoveredName, isAlert, isPreselected } from '@/lib/statements';
import { StatementAlertList, uploadRows, useClearWhenDone } from './StatementAlerts';
import { StatementDetail } from './StatementDetail';
import { StatusPill } from './StatusPill';

/** Header uploads share one list; alert cards keep their own. */
export const PAGE_UPLOAD_SCOPE = 'statements-page';

/** Rows enter and leave with a short fade and slide, and the list closes the gap smoothly. */
const ROW_MOTION = {
  layout: 'position',
  initial: { opacity: 0, y: -4 },
  animate: { opacity: 1, y: 0 },
  exit: { opacity: 0, transition: { duration: 0.15 } },
  transition: { type: 'spring', stiffness: 500, damping: 40 },
} as const;

function periodText(statement: Statement, dateFormat: string): string {
  if (statement.periodStart && statement.periodEnd) {
    return `${formatDate(statement.periodStart, dateFormat)} – ${formatDate(statement.periodEnd, dateFormat)}`;
  }
  return statement.filename;
}

/** Gmail connection and scanning. Also finishes the Google consent round trip (?gmail=…) by announcing the outcome. */
function GmailCard() {
  const session = useSession();
  const gmail = useGmail();
  const jobs = useJobs();
  const toast = useToast();
  const client = useQueryClient();
  const isDemo = session.data?.user?.isDemo ?? false;
  const available = session.data?.capabilities.gmail ?? false;

  const sync = useMutation({ mutationFn: api.syncStatements, onSuccess: ({ jobId }) => jobs.track(jobId) });
  const once = useSingleFlight();

  useGmailReturn((outcome) => {
    const message = GMAIL_OUTCOME_MESSAGES[outcome];
    toast(message.text, message.tone);
    if (outcome === 'connected') {
      void client.invalidateQueries({ queryKey: keys.gmail });
      sync.mutate();
    }
  });

  if (isDemo || !available) {
    return null;
  }

  const connection = gmail.data;
  const loading = gmail.isPending;

  return (
    <Card className="flex flex-col gap-4 p-5 sm:flex-row sm:items-center md:p-6" aria-labelledby="gmail-title" aria-busy={loading}>
      <span className="flex size-11 shrink-0 items-center justify-center rounded-xl bg-accent-soft text-accent" aria-hidden="true">
        <Mail size={22} />
      </span>
      <div className="min-w-0 flex-1">
        <h2 id="gmail-title" className="text-[1.0625rem] font-semibold tracking-[-0.01em]">
          {loading ? 'Gmail' : !connection?.connected ? 'Connect your Gmail' : connection.status === 'expired' ? 'Gmail connection expired' : 'Gmail'}
        </h2>
        <p className="text-[0.9375rem] text-label-secondary">
          {loading ? (
            <Skeleton className="my-[0.2em] h-[1em] w-60 max-w-full" />
          ) : !connection?.connected ? (
            'FinSight can automatically find bank statements in your inbox. Access is read-only.'
          ) : connection.status === 'expired' ? (
            'Reconnect your account to keep finding new statements.'
          ) : (
            `${connection.email}${connection.lastSyncedAt ? ` · scanned ${formatRelativeTime(connection.lastSyncedAt)}` : ''}`
          )}
        </p>
        {sync.isError && (
          <p role="alert" className="mt-1 text-[0.875rem] text-critical">
            {errorMessage(sync.error)}
          </p>
        )}
      </div>
      {loading ? (
        <Skeleton className="h-10 w-32 rounded-full" />
      ) : !connection?.connected || connection.status === 'expired' ? (
        <a href={gmailConnectUrl('/statements')} className={buttonStyles()}>
          {connection?.connected ? 'Reconnect' : 'Connect Gmail'}
        </a>
      ) : (
        <Button variant="secondary" icon={<RefreshCw size={16} aria-hidden="true" />} loading={sync.isPending || (jobs.isActive && jobs.job?.kind === 'sync')} onClick={() => void once(() => sync.mutateAsync())}>
          Scan Gmail
        </Button>
      )}
    </Card>
  );
}

function StatementRow({ statement, dateFormat, selectable, selected, onToggle, onOpen }: { statement: Statement; dateFormat: string; selectable?: boolean; selected?: boolean; onToggle?: () => void; onOpen: () => void }) {
  return (
    <motion.li className="flex items-center" {...ROW_MOTION}>
      {selectable && (
        <label className="flex h-full items-center py-4 pl-4 md:pl-5">
          <span className="sr-only">Select {statement.title}</span>
          <RoundCheckbox checked={selected ?? false} onChange={() => onToggle?.()} />
        </label>
      )}
      <button type="button" onClick={onOpen} className="flex min-w-0 flex-1 items-center gap-3.5 px-4 py-3.5 text-left transition-colors hover:bg-fill md:px-5">
        <span className="flex size-10 shrink-0 items-center justify-center rounded-xl bg-fill text-label-secondary" aria-hidden="true">
          <FileText size={19} />
        </span>
        <div className="min-w-0 flex-1">
          <TruncatedText as="p" className="text-[0.9375rem] font-medium">
            {statement.status === 'discovered' ? discoveredName(statement) : statement.title}
          </TruncatedText>
          <p className="caption truncate">
            {statement.status === 'discovered' ? `${statement.title} · ${statement.filename}` : `${accountLabel(statement.institution, statement.accountMask)} · ${periodText(statement, dateFormat)}`}
          </p>
          {statement.status === 'failed' && statement.failureMessage && <p className="mt-0.5 text-[0.8125rem] text-critical">{statement.failureMessage}</p>}
        </div>
        <div className="hidden shrink-0 flex-col items-end gap-1 sm:flex">
          <StatusPill status={statement.status} />
          {statement.status === 'processed' && <span className="caption">{statement.transactionCount} transactions</span>}
        </div>
        <ChevronRight size={17} className="shrink-0 text-label-tertiary" aria-hidden="true" />
      </button>
    </motion.li>
  );
}

function ListSkeleton() {
  return (
    <section aria-hidden="true">
      <Skeleton className="mx-1 mb-3 h-6 w-28" />
      <div className="card grouped overflow-hidden">
        {Array.from({ length: 5 }, (_, i) => (
          <div key={i} className="flex items-center gap-3.5 px-4 py-3.5 md:px-5">
            <Skeleton className="size-10 shrink-0 rounded-xl" />
            <span className="min-w-0 flex-1 space-y-1.5">
              <Skeleton className="h-3.5 w-40 max-w-[60%]" />
              <Skeleton className="h-3 w-56 max-w-[80%]" />
            </span>
            <Skeleton className="hidden h-6 w-20 rounded-full sm:block" />
          </div>
        ))}
      </div>
    </section>
  );
}

function StatementSection({ id, title, subtitle, action, children }: { id: string; title: string; subtitle?: string; action?: ReactNode; children: ReactNode }) {
  return (
    <motion.section aria-labelledby={id} layout="position" transition={{ type: 'spring', stiffness: 500, damping: 40 }}>
      <div className="mb-3 flex flex-wrap items-end justify-between gap-3 px-1">
        <div>
          <h2 id={id} className="title-section">
            {title}
          </h2>
          {subtitle && <p className="caption mt-0.5">{subtitle}</p>}
        </div>
        {action}
      </div>
      {children}
    </motion.section>
  );
}

export default function StatementsPage() {
  const statements = useStatements();
  const prefs = usePreferences();
  const jobs = useJobs();
  const [params] = useSearchParams();
  const [openId, setOpenId] = useState<string | null>(null);
  const [selection, setSelection] = useState<Set<string> | null>(null);
  const once = useSingleFlight();

  const all = statements.data ?? [];
  // Alerts (statements the bank said are ready but didn't attach) have their own section and never mix with the rest.
  const alerts = all.filter(isAlert);
  const showAlerts = alerts.length > 0 || jobs.uploads.some((item) => item.scope.startsWith('alert:'));
  const discovered = all.filter((s) => s.status === 'discovered');
  const inProgress = all.filter((s) => s.status === 'downloading' || s.status === 'processing');
  const failed = all.filter((s) => s.status === 'failed');
  const processed = all.filter((s) => s.status === 'processed');

  // Statements look selected by default, except likely pay stubs and weak matches.
  const selected = selection ?? new Set(discovered.filter(isPreselected).map((s) => s.id));

  const process = useMutation({
    mutationFn: (ids: string[]) => api.processStatements(ids),
    onSuccess: ({ jobId }) => {
      jobs.track(jobId);
      setSelection(null);
    },
  });

  // Uploads from the header go one at a time; the server matches each to a waiting alert when it can.
  const pageUploads = jobs.uploads.filter((item) => item.scope === PAGE_UPLOAD_SCOPE);
  useClearWhenDone(PAGE_UPLOAD_SCOPE, pageUploads);
  const picker = usePdfPicker((files) => void jobs.uploadFiles(files, { scope: PAGE_UPLOAD_SCOPE }));
  const uploadDetail = (statementId: string | null) => {
    const statement = all.find((s) => s.id === statementId);
    return statement?.status === 'processed' ? `${statement.transactionCount} transactions` : null;
  };

  function toggle(id: string) {
    const next = new Set(selected);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setSelection(next);
  }

  const rows = (list: Statement[], selectable = false) => (
    <GroupedList>
      <AnimatePresence initial={false}>
        {list.map((s) => (
          <StatementRow
            key={s.id}
            statement={s}
            dateFormat={prefs.dateFormat}
            selectable={selectable}
            selected={selectable ? selected.has(s.id) : undefined}
            onToggle={selectable ? () => toggle(s.id) : undefined}
            onOpen={() => setOpenId(s.id)}
          />
        ))}
      </AnimatePresence>
    </GroupedList>
  );

  return (
    <div>
      <PageHeader
        title="Statements"
        subtitle="Statements found in Gmail or uploaded by you."
        actions={
          <>
            {picker.input}
            {/* Arriving from "Upload a PDF" elsewhere highlights this button; the file picker needs a click of its own. */}
            <Button variant={params.get('upload') ? 'primary' : 'secondary'} icon={<FileUp size={16} aria-hidden="true" />} onClick={picker.open}>
              Upload PDFs
            </Button>
          </>
        }
      />

      <Collapse open={pageUploads.length > 0}>
        <Card className="mb-8 rounded-[20px] px-4 pt-3.5 pb-4 md:px-5">
          <h2 className="eyebrow mb-2 px-1">{pageUploads.some((item) => item.state !== 'done' && item.state !== 'failed') ? 'Uploading' : 'Uploaded'}</h2>
          <UploadProgressList
            label="Uploaded PDFs"
            rows={uploadRows(pageUploads, (id) => jobs.clearUploads(PAGE_UPLOAD_SCOPE, id), (item) => uploadDetail(item.statementId))}
          />
        </Card>
      </Collapse>

      <div className="space-y-8">
        <GmailCard />

        {statements.isPending ? (
          <ListSkeleton />
        ) : statements.isError ? (
          <Card>
            <ErrorState message={errorMessage(statements.error)} onRetry={() => void statements.refetch()} />
          </Card>
        ) : all.length === 0 ? (
          <Card>
            <EmptyState
              icon={<FileText size={26} aria-hidden="true" />}
              title="No statements yet"
              description="Scan Gmail to find your bank statements, or use Upload PDF to add a statement downloaded from your bank’s website."
            />
          </Card>
        ) : (
          <AnimatePresence initial={false}>
            {showAlerts && (
              <StatementSection key="alerts" id="alerts-title" title="Waiting for you to download" subtitle="Your bank emailed that these statements are ready but didn’t attach them.">
                <StatementAlertList statements={all} dateFormat={prefs.dateFormat} />
              </StatementSection>
            )}

            {discovered.length > 0 && (
              <StatementSection
                key="ready"
                id="ready-title"
                title="Ready to analyze"
                subtitle={`${discovered.length} found in Gmail. Choose which to import.`}
                action={
                  // Disabled by a job running elsewhere (its panel may be hidden), a tag says why. An empty selection needs no tag.
                  <Tooltip wrap disabled={!jobs.isActive || process.isPending} content="Available when the current import or scan finishes">
                    <Button disabled={selected.size === 0 || jobs.isActive} loading={process.isPending} onClick={() => void once(() => process.mutateAsync([...selected]))}>
                      Analyze {selected.size} {selected.size === 1 ? 'statement' : 'statements'}
                    </Button>
                  </Tooltip>
                }
              >
                {process.isError && (
                  <p role="alert" className="mb-3 px-1 text-[0.9375rem] text-critical">
                    {errorMessage(process.error)}
                  </p>
                )}
                {rows(discovered, true)}
              </StatementSection>
            )}

            {inProgress.length > 0 && (
              <StatementSection key="progress" id="progress-title" title="In progress">
                {rows(inProgress)}
              </StatementSection>
            )}

            {failed.length > 0 && (
              <StatementSection key="failed" id="failed-title" title="Needs attention">
                {rows(failed)}
              </StatementSection>
            )}

            {processed.length > 0 && (
              <StatementSection key="processed" id="processed-title" title="Analyzed">
                {rows(processed)}
              </StatementSection>
            )}
          </AnimatePresence>
        )}
      </div>

      <StatementDetail statementId={openId} dateFormat={prefs.dateFormat} currency={prefs.currency} onClose={() => setOpenId(null)} />
    </div>
  );
}
