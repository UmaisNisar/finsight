import { useMutation } from '@tanstack/react-query';
import { FileText, RotateCw, Trash2, Upload } from 'lucide-react';
import { useRef, type ReactNode } from 'react';
import { errorMessage } from '@/api/client';
import { api } from '@/api/endpoints';
import { useInvalidateFinancialData, useStatement } from '@/api/queries';
import type { Statement } from '@/api/schemas';
import { useConfirm } from '@/app/providers/ConfirmProvider';
import { useJobs } from '@/app/providers/JobsProvider';
import { useToast } from '@/app/providers/ToastProvider';
import { Button } from '@/components/ui/Button';
import { Sheet } from '@/components/ui/Sheet';
import { ErrorState, Skeleton } from '@/components/ui/primitives';
import { useRetained } from '@/hooks/useRetained';
import { useSingleFlight } from '@/hooks/useSingleFlight';
import { CategoryGlyph } from '@/lib/categories';
import { cn } from '@/lib/cn';
import { accountLabel, formatDate, formatMoney } from '@/lib/format';
import { StatusPill } from './StatusPill';

const ACCOUNT_TYPE: Record<Statement['accountType'], string> = {
  unknown: 'Account',
  chequing: 'Chequing',
  savings: 'Savings',
  creditCard: 'Credit card',
  lineOfCredit: 'Line of credit',
  investment: 'Investment',
};

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-baseline justify-between gap-4 px-4 py-3">
      <dt className="text-[0.9375rem] text-label-secondary">{label}</dt>
      <dd className="m-0 min-w-0 text-right text-[0.9375rem] break-words">{children}</dd>
    </div>
  );
}

function DetailSkeleton() {
  const rows = (count: number) => (
    <div className="card grouped overflow-hidden">
      {Array.from({ length: count }, (_, i) => (
        <div key={i} className="flex items-center justify-between px-4 py-3">
          <Skeleton className="my-[3px] h-4 w-24" />
          <Skeleton className="h-4 w-32" />
        </div>
      ))}
    </div>
  );
  return (
    <div className="space-y-6" aria-hidden="true">
      <Skeleton className="h-6 w-24 rounded-full" />
      {rows(4)}
      <div>
        <Skeleton className="mx-1 mb-2 h-3 w-14" />
        {rows(3)}
      </div>
    </div>
  );
}

/** A statement's details and actions. Opens for `statementId`; keeps the last statement while it animates closed. */
export function StatementDetail({ statementId, dateFormat, currency, onClose }: { statementId: string | null; dateFormat: string; currency: string; onClose: () => void }) {
  const { item, open, key } = useRetained(statementId);
  if (!item) return null;
  // A fresh sheet per opening, so an error from one statement's action never shows on another.
  return <StatementSheet key={key} statementId={item} open={open} dateFormat={dateFormat} currency={currency} onClose={onClose} />;
}

function StatementSheet({ statementId, open, dateFormat, currency, onClose }: { statementId: string; open: boolean; dateFormat: string; currency: string; onClose: () => void }) {
  const detail = useStatement(statementId, open);
  const jobs = useJobs();
  const toast = useToast();
  const confirm = useConfirm();
  const invalidate = useInvalidateFinancialData();
  const fileInput = useRef<HTMLInputElement>(null);
  const once = useSingleFlight();

  const reprocess = useMutation({
    mutationFn: (id: string) => api.processStatements([id]),
    onSuccess: ({ jobId }) => {
      jobs.track(jobId);
      onClose();
    },
  });

  const reupload = useMutation({
    mutationFn: ({ file, id }: { file: File; id: string }) => api.uploadStatement(file, id),
    onSuccess: ({ jobId }) => {
      jobs.track(jobId);
      onClose();
    },
  });

  const remove = useMutation({
    mutationFn: (id: string) => api.deleteStatement(id),
    onSuccess: async () => {
      onClose();
      toast('Statement and its transactions deleted', 'success');
      await invalidate();
    },
  });

  const data = detail.data;
  const statement = data?.statement;
  const actionError = reprocess.error ?? reupload.error ?? remove.error;

  return (
    <Sheet
      open={open}
      onClose={onClose}
      size="lg"
      title={statement ? statement.title : detail.isPending ? <Skeleton className="my-[0.1em] h-[1em] w-56" /> : 'Statement'}
      subtitle={statement ? accountLabel(statement.institution, statement.accountMask) : detail.isPending ? <Skeleton className="my-[0.2em] h-[1em] w-32" /> : undefined}
      footer={
        statement ? (
          <>
            <Button
              variant="destructive-plain"
              icon={<Trash2 size={16} aria-hidden="true" />}
              loading={remove.isPending}
              onClick={() =>
                void once(async () => {
                  const ok = await confirm({
                    title: 'Delete this statement?',
                    message: 'The statement and all of its transactions will be removed. This can’t be undone.',
                    confirmLabel: 'Delete',
                    destructive: true,
                  });
                  if (ok) await remove.mutateAsync(statement.id);
                })
              }
            >
              Delete
            </Button>
            <div className="ml-auto flex gap-2">
              {statement.reprocessNeedsUpload ? (
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
                      if (file) void once(() => reupload.mutateAsync({ file, id: statement.id }));
                      e.target.value = '';
                    }}
                  />
                  <Button icon={<Upload size={16} aria-hidden="true" />} loading={reupload.isPending} onClick={() => fileInput.current?.click()}>
                    Upload again
                  </Button>
                </>
              ) : (
                statement.canReprocess && (
                  <Button icon={<RotateCw size={16} aria-hidden="true" />} loading={reprocess.isPending} onClick={() => void once(() => reprocess.mutateAsync(statement.id))}>
                    {statement.status === 'discovered' ? 'Analyze' : 'Reprocess'}
                  </Button>
                )
              )}
            </div>
          </>
        ) : detail.isPending ? (
          <Skeleton className="h-10 w-24 rounded-full" />
        ) : undefined
      }
    >
      {detail.isPending ? (
        <DetailSkeleton />
      ) : detail.isError || !data || !statement ? (
        <ErrorState message={errorMessage(detail.error)} onRetry={() => void detail.refetch()} />
      ) : (
        <div className="fade-in space-y-6">
          <div className="flex flex-wrap items-center gap-2">
            <StatusPill status={statement.status} />
            {statement.extractionConfidence !== null && statement.status === 'processed' && (
              <span className="caption">Extraction confidence {Math.round(statement.extractionConfidence * 100)}%</span>
            )}
          </div>

          {statement.failureMessage && (
            <p role="alert" className="rounded-2xl bg-critical-soft px-4 py-3 text-[0.9375rem] text-critical">
              {statement.failureMessage}
            </p>
          )}

          {data.warnings.length > 0 && (
            <ul className="space-y-1.5 rounded-2xl bg-attention-soft px-4 py-3 text-[0.875rem]">
              {data.warnings.map((warning) => (
                <li key={warning}>{warning}</li>
              ))}
            </ul>
          )}

          <dl className="card grouped m-0 overflow-hidden">
            <Row label="Account">
              {ACCOUNT_TYPE[statement.accountType]}
              {statement.accountMask && ` ••${statement.accountMask}`}
            </Row>
            {statement.periodStart && statement.periodEnd && (
              <Row label="Period">
                {formatDate(statement.periodStart, dateFormat)} – {formatDate(statement.periodEnd, dateFormat)}
              </Row>
            )}
            {data.openingBalance !== null && <Row label="Opening balance">{formatMoney(data.openingBalance, data.currency ?? currency)}</Row>}
            {data.closingBalance !== null && <Row label="Closing balance">{formatMoney(data.closingBalance, data.currency ?? currency)}</Row>}
            <Row label="Transactions">{statement.transactionCount}</Row>
          </dl>

          <section aria-labelledby="source-title">
            <h3 id="source-title" className="eyebrow mb-2 px-1">
              Source
            </h3>
            <dl className="card grouped m-0 overflow-hidden">
              <Row label="From">{statement.source === 'manualUpload' ? 'Uploaded by you' : statement.source === 'demo' ? 'Sample data' : (statement.senderName ?? 'Gmail')}</Row>
              {data.subject && <Row label="Subject">{data.subject}</Row>}
              {statement.receivedAt && <Row label={statement.source === 'manualUpload' ? 'Uploaded' : 'Received'}>{formatDate(statement.receivedAt.slice(0, 10), dateFormat)}</Row>}
              <Row label="File">
                <span className="inline-flex items-center gap-1.5">
                  <FileText size={14} aria-hidden="true" className="shrink-0 text-label-tertiary" />
                  {statement.filename}
                </span>
              </Row>
            </dl>
            {data.detectionReasons.length > 0 && statement.source === 'gmail' && (
              <p className="caption mt-2 px-1">
                Detected as a statement ({Math.round(statement.detectionConfidence * 100)}%): {data.detectionReasons.join(' · ')}
              </p>
            )}
          </section>

          {data.transactions.length > 0 && (
            <section aria-labelledby="extracted-title">
              <h3 id="extracted-title" className="eyebrow mb-2 px-1">
                Extracted transactions
              </h3>
              <ul className="card grouped overflow-hidden">
                {data.transactions.map((t) => (
                  <li key={t.id} className="flex items-center gap-3 px-4 py-2.5">
                    <CategoryGlyph groupId={t.groupId} type={t.type} size={30} />
                    <div className="min-w-0 flex-1">
                      <p className="truncate text-[0.875rem]">{t.merchant}</p>
                      <p className="caption truncate">
                        {formatDate(t.date, dateFormat)} · {t.type === 'transfer' ? 'Transfer' : t.categoryName}
                      </p>
                    </div>
                    <p className={cn('tabular text-[0.875rem]', t.type === 'income' && 'text-positive')}>{formatMoney(t.amount, t.currency, { signed: t.amount > 0 })}</p>
                  </li>
                ))}
              </ul>
            </section>
          )}

          {actionError && (
            <p role="alert" className="text-[0.9375rem] text-critical">
              {errorMessage(actionError)}
            </p>
          )}
        </div>
      )}
    </Sheet>
  );
}
