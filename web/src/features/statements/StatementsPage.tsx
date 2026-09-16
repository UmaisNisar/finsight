import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Check, ChevronRight, FileText, FileUp, Mail, RefreshCw } from 'lucide-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router';
import { errorMessage } from '@/api/client';
import { api } from '@/api/endpoints';
import { keys, useGmail, useSession, useStatements } from '@/api/queries';
import type { Statement } from '@/api/schemas';
import { useJobs } from '@/app/providers/JobsProvider';
import { useToast } from '@/app/providers/ToastProvider';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/Button';
import { Card, EmptyState, ErrorState, Skeleton } from '@/components/ui/primitives';
import { usePreferences } from '@/hooks/usePreferences';
import { cn } from '@/lib/cn';
import { accountLabel, formatDate, formatRelativeTime } from '@/lib/format';
import { StatementDetail } from './StatementDetail';
import { StatusPill } from './StatusPill';

const GMAIL_MESSAGES: Record<string, { text: string; tone: 'success' | 'error' }> = {
  connected: { text: 'Gmail connected. Looking for statements…', tone: 'success' },
  denied: { text: 'FinSight needs permission to read Gmail to find statements. Nothing was connected.', tone: 'error' },
  failed: { text: 'Connecting Gmail didn’t complete. Try again.', tone: 'error' },
  unavailable: { text: 'Gmail integration isn’t set up on this server.', tone: 'error' },
  demo: { text: 'Demo mode uses sample statements. Sign in with Google to use your Gmail.', tone: 'error' },
};

function periodText(statement: Statement, dateFormat: string): string {
  if (statement.periodStart && statement.periodEnd) {
    return `${formatDate(statement.periodStart, dateFormat)} – ${formatDate(statement.periodEnd, dateFormat)}`;
  }
  return statement.filename;
}

function GmailCard() {
  const session = useSession();
  const gmail = useGmail();
  const jobs = useJobs();
  const isDemo = session.data?.user?.isDemo ?? false;
  const available = session.data?.capabilities.gmail ?? false;

  const sync = useMutation({ mutationFn: api.syncStatements, onSuccess: ({ jobId }) => jobs.track(jobId) });

  if (isDemo || !available) {
    return null;
  }

  if (gmail.isPending) {
    return <Skeleton className="h-24 w-full rounded-[20px]" />;
  }

  const connection = gmail.data;

  return (
    <Card className="flex flex-col gap-4 p-5 sm:flex-row sm:items-center md:p-6" aria-labelledby="gmail-title">
      <span className="flex size-11 shrink-0 items-center justify-center rounded-xl bg-accent-soft text-accent" aria-hidden="true">
        <Mail size={22} />
      </span>
      <div className="min-w-0 flex-1">
        <h2 id="gmail-title" className="text-[1.0625rem] font-semibold tracking-[-0.01em]">
          {!connection?.connected ? 'Connect your Gmail' : connection.status === 'expired' ? 'Gmail connection expired' : 'Gmail'}
        </h2>
        <p className="text-[0.9375rem] text-label-secondary">
          {!connection?.connected
            ? 'FinSight can automatically find bank statements in your inbox. Access is read-only.'
            : connection.status === 'expired'
              ? 'Reconnect your account to keep finding new statements.'
              : `${connection.email}${connection.lastSyncedAt ? ` · scanned ${formatRelativeTime(connection.lastSyncedAt)}` : ''}`}
        </p>
        {sync.isError && (
          <p role="alert" className="mt-1 text-[0.875rem] text-critical">
            {errorMessage(sync.error)}
          </p>
        )}
      </div>
      {!connection?.connected || connection.status === 'expired' ? (
        <a href="/api/gmail/connect" className="inline-flex h-10 items-center justify-center rounded-full bg-accent px-5 text-[0.9375rem] font-medium text-accent-contrast">
          {connection?.connected ? 'Reconnect' : 'Connect Gmail'}
        </a>
      ) : (
        <Button variant="secondary" icon={<RefreshCw size={16} aria-hidden="true" />} loading={sync.isPending || (jobs.isActive && jobs.job?.kind === 'sync')} onClick={() => sync.mutate()}>
          Scan Gmail
        </Button>
      )}
    </Card>
  );
}

function StatementRow({ statement, dateFormat, selectable, selected, onToggle, onOpen }: { statement: Statement; dateFormat: string; selectable?: boolean; selected?: boolean; onToggle?: () => void; onOpen: () => void }) {
  return (
    <li className="flex items-center">
      {selectable && (
        <label className="flex h-full items-center py-4 pl-4 md:pl-5">
          <span className="sr-only">Select {statement.title}</span>
          <input type="checkbox" checked={selected} onChange={onToggle} className="peer sr-only" />
          <span
            aria-hidden="true"
            className={cn(
              'flex size-[22px] items-center justify-center rounded-full border-[1.5px] transition-colors peer-focus-visible:outline-3 peer-focus-visible:outline-accent/50',
              selected ? 'border-accent bg-accent text-white' : 'border-label-tertiary/60',
            )}
          >
            {selected && <Check size={13} strokeWidth={3} />}
          </span>
        </label>
      )}
      <button type="button" onClick={onOpen} className="flex min-w-0 flex-1 items-center gap-3.5 px-4 py-3.5 text-left transition-colors hover:bg-fill md:px-5">
        <span className="flex size-10 shrink-0 items-center justify-center rounded-xl bg-fill text-label-secondary" aria-hidden="true">
          <FileText size={19} />
        </span>
        <div className="min-w-0 flex-1">
          <p className="truncate text-[0.9375rem] font-medium">
            {statement.status === 'discovered' ? (statement.institution ?? statement.senderName ?? 'Possible statement') : statement.title}
          </p>
          <p className="caption truncate">
            {statement.status === 'discovered'
              ? `${statement.title} · ${statement.filename}`
              : `${accountLabel(statement.institution, statement.accountMask)} · ${periodText(statement, dateFormat)}`}
          </p>
          {statement.status === 'failed' && statement.failureMessage && <p className="mt-0.5 text-[0.8125rem] text-critical">{statement.failureMessage}</p>}
        </div>
        <div className="hidden shrink-0 flex-col items-end gap-1 sm:flex">
          <StatusPill status={statement.status} />
          {statement.status === 'processed' && <span className="caption">{statement.transactionCount} transactions</span>}
        </div>
        <ChevronRight size={17} className="shrink-0 text-label-tertiary" aria-hidden="true" />
      </button>
    </li>
  );
}

export default function StatementsPage() {
  const statements = useStatements();
  const prefs = usePreferences();
  const jobs = useJobs();
  const toast = useToast();
  const client = useQueryClient();
  const [params, setParams] = useSearchParams();
  const [openId, setOpenId] = useState<string | null>(null);
  const [selection, setSelection] = useState<Set<string> | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);

  const all = useMemo(() => statements.data ?? [], [statements.data]);
  const discovered = all.filter((s) => s.status === 'discovered');
  const inProgress = all.filter((s) => s.status === 'downloading' || s.status === 'processing');
  const failed = all.filter((s) => s.status === 'failed');
  const processed = all.filter((s) => s.status === 'processed');

  // Statements look selected by default, except likely pay stubs and weak matches.
  const selected = selection ?? new Set(discovered.filter((s) => s.documentKind !== 'incomeDocument' && s.detectionConfidence >= 0.6).map((s) => s.id));

  const sync = useMutation({ mutationFn: api.syncStatements, onSuccess: ({ jobId }) => jobs.track(jobId) });
  const process = useMutation({
    mutationFn: (ids: string[]) => api.processStatements(ids),
    onSuccess: ({ jobId }) => {
      jobs.track(jobId);
      setSelection(null);
    },
  });
  const upload = useMutation({
    mutationFn: (file: File) => api.uploadStatement(file),
    onSuccess: ({ jobId }) => {
      jobs.track(jobId);
      void client.invalidateQueries({ queryKey: keys.statements });
    },
  });

  // Returning from Google's consent screen.
  const gmailStatus = params.get('gmail');
  const handled = useRef(false);
  useEffect(() => {
    if (!gmailStatus || handled.current) return;
    handled.current = true;
    const message = GMAIL_MESSAGES[gmailStatus];
    if (message) toast(message.text, message.tone);
    if (gmailStatus === 'connected') {
      void client.invalidateQueries({ queryKey: keys.gmail });
      sync.mutate();
    }
    setParams(
      (current) => {
        const next = new URLSearchParams(current);
        next.delete('gmail');
        return next;
      },
      { replace: true },
    );
  }, [gmailStatus, client, setParams, sync, toast]);

  function toggle(id: string) {
    const next = new Set(selected);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setSelection(next);
  }

  const busy = jobs.isActive;

  return (
    <div>
      <PageHeader
        title="Statements"
        subtitle="Statements found in Gmail or uploaded by you."
        actions={
          <>
            <input
              ref={fileInput}
              type="file"
              accept="application/pdf,.pdf"
              className="sr-only"
              tabIndex={-1}
              aria-hidden="true"
              onChange={(e) => {
                const file = e.target.files?.[0];
                if (file) upload.mutate(file);
                e.target.value = '';
              }}
            />
            <Button variant={params.get('upload') ? 'primary' : 'secondary'} icon={<FileUp size={16} aria-hidden="true" />} loading={upload.isPending} onClick={() => fileInput.current?.click()}>
              Upload PDF
            </Button>
          </>
        }
      />

      {upload.isError && (
        <p role="alert" className="mb-4 rounded-2xl bg-critical-soft px-4 py-3 text-[0.9375rem] text-critical">
          {errorMessage(upload.error)}
        </p>
      )}

      <div className="space-y-8">
        <GmailCard />

        {statements.isPending ? (
          <Skeleton className="h-64 w-full rounded-[20px]" />
        ) : statements.isError ? (
          <Card>
            <ErrorState message={errorMessage(statements.error)} onRetry={() => void statements.refetch()} />
          </Card>
        ) : all.length === 0 ? (
          <Card>
            <EmptyState
              icon={<FileText size={26} aria-hidden="true" />}
              title="No statements yet"
              description="Scan Gmail to find your bank statements, or upload a PDF statement from your bank’s website."
              action={
                <Button icon={<FileUp size={16} aria-hidden="true" />} onClick={() => fileInput.current?.click()}>
                  Upload a PDF
                </Button>
              }
            />
          </Card>
        ) : (
          <>
            {discovered.length > 0 && (
              <section aria-labelledby="ready-title">
                <div className="mb-3 flex flex-wrap items-end justify-between gap-3 px-1">
                  <div>
                    <h2 id="ready-title" className="title-section">
                      Ready to analyze
                    </h2>
                    <p className="caption mt-0.5">
                      {discovered.length} found in Gmail. Choose which to import.
                    </p>
                  </div>
                  <Button disabled={selected.size === 0 || busy} loading={process.isPending} onClick={() => process.mutate([...selected])}>
                    Analyze {selected.size} {selected.size === 1 ? 'statement' : 'statements'}
                  </Button>
                </div>
                {process.isError && (
                  <p role="alert" className="mb-3 px-1 text-[0.9375rem] text-critical">
                    {errorMessage(process.error)}
                  </p>
                )}
                <ul className="card overflow-hidden [&>li+li]:shadow-[inset_0_0.5px_0_var(--separator)]">
                  {discovered.map((s) => (
                    <StatementRow key={s.id} statement={s} dateFormat={prefs.dateFormat} selectable selected={selected.has(s.id)} onToggle={() => toggle(s.id)} onOpen={() => setOpenId(s.id)} />
                  ))}
                </ul>
              </section>
            )}

            {(failed.length > 0 || inProgress.length > 0) && (
              <section aria-labelledby="attention-title">
                <h2 id="attention-title" className="title-section mb-3 px-1">
                  {inProgress.length > 0 ? 'In progress' : 'Needs attention'}
                </h2>
                <ul className="card overflow-hidden [&>li+li]:shadow-[inset_0_0.5px_0_var(--separator)]">
                  {[...inProgress, ...failed].map((s) => (
                    <StatementRow key={s.id} statement={s} dateFormat={prefs.dateFormat} onOpen={() => setOpenId(s.id)} />
                  ))}
                </ul>
              </section>
            )}

            {processed.length > 0 && (
              <section aria-labelledby="processed-title">
                <h2 id="processed-title" className="title-section mb-3 px-1">
                  Analyzed
                </h2>
                <ul className="card overflow-hidden [&>li+li]:shadow-[inset_0_0.5px_0_var(--separator)]">
                  {processed.map((s) => (
                    <StatementRow key={s.id} statement={s} dateFormat={prefs.dateFormat} onOpen={() => setOpenId(s.id)} />
                  ))}
                </ul>
              </section>
            )}
          </>
        )}

        {sync.isError && (
          <p role="alert" className="text-[0.9375rem] text-critical">
            {errorMessage(sync.error)}
          </p>
        )}
      </div>

      <StatementDetail statementId={openId} dateFormat={prefs.dateFormat} currency={prefs.currency} onClose={() => setOpenId(null)} />
    </div>
  );
}
