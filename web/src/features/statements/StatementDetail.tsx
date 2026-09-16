import { useMutation } from '@tanstack/react-query';
import { FileText, RotateCw, Trash2, Upload } from 'lucide-react';
import { useRef } from 'react';
import { errorMessage } from '@/api/client';
import { api } from '@/api/endpoints';
import { useInvalidateFinancialData, useStatement } from '@/api/queries';
import type { Statement } from '@/api/schemas';
import { useJobs } from '@/app/providers/JobsProvider';
import { useToast } from '@/app/providers/ToastProvider';
import { Button } from '@/components/ui/Button';
import { Sheet } from '@/components/ui/Sheet';
import { ErrorState, Skeleton } from '@/components/ui/primitives';
import { CategoryGlyph } from '@/lib/categories';
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

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex items-baseline justify-between gap-4 px-4 py-3">
      <dt className="text-[0.9375rem] text-label-secondary">{label}</dt>
      <dd className="m-0 text-right text-[0.9375rem]">{children}</dd>
    </div>
  );
}

export function StatementDetail({ statementId, dateFormat, currency, onClose }: { statementId: string | null; dateFormat: string; currency: string; onClose: () => void }) {
  const detail = useStatement(statementId);
  const jobs = useJobs();
  const toast = useToast();
  const invalidate = useInvalidateFinancialData();
  const fileInput = useRef<HTMLInputElement>(null);

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
      await invalidate();
      toast('Statement and its transactions deleted', 'success');
      onClose();
    },
  });

  const data = detail.data;
  const statement = data?.statement;
  const actionError = reprocess.error ?? reupload.error ?? remove.error;

  return (
    <Sheet
      open={statementId !== null}
      onClose={onClose}
      size="lg"
      title={statement ? statement.title : 'Statement'}
      subtitle={statement ? accountLabel(statement.institution, statement.accountMask) : undefined}
      footer={
        statement && (
          <>
            <Button
              variant="destructive-plain"
              icon={<Trash2 size={16} aria-hidden="true" />}
              loading={remove.isPending}
              onClick={() => {
                if (window.confirm('Delete this statement and all of its transactions? This can’t be undone.')) {
                  remove.mutate(statement.id);
                }
              }}
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
                      if (file) reupload.mutate({ file, id: statement.id });
                      e.target.value = '';
                    }}
                  />
                  <Button icon={<Upload size={16} aria-hidden="true" />} loading={reupload.isPending} onClick={() => fileInput.current?.click()}>
                    Upload again
                  </Button>
                </>
              ) : (
                statement.canReprocess && (
                  <Button icon={<RotateCw size={16} aria-hidden="true" />} loading={reprocess.isPending} onClick={() => reprocess.mutate(statement.id)}>
                    {statement.status === 'discovered' ? 'Analyze' : 'Reprocess'}
                  </Button>
                )
              )}
            </div>
          </>
        )
      }
    >
      {detail.isPending ? (
        <div className="space-y-3" aria-busy="true">
          <Skeleton className="h-24 w-full rounded-2xl" />
          <Skeleton className="h-48 w-full rounded-2xl" />
        </div>
      ) : detail.isError || !data || !statement ? (
        <ErrorState message={errorMessage(detail.error)} onRetry={() => void detail.refetch()} />
      ) : (
        <div className="space-y-6">
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

          <dl className="card m-0 overflow-hidden [&>div+div]:shadow-[inset_0_0.5px_0_var(--separator)]">
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
            <dl className="card m-0 overflow-hidden [&>div+div]:shadow-[inset_0_0.5px_0_var(--separator)]">
              <Row label="From">{statement.source === 'manualUpload' ? 'Uploaded by you' : statement.source === 'demo' ? 'Sample data' : (statement.senderName ?? 'Gmail')}</Row>
              {data.subject && <Row label="Subject">{data.subject}</Row>}
              {statement.receivedAt && <Row label={statement.source === 'manualUpload' ? 'Uploaded' : 'Received'}>{formatDate(statement.receivedAt.slice(0, 10), dateFormat)}</Row>}
              <Row label="File">
                <span className="inline-flex items-center gap-1.5">
                  <FileText size={14} aria-hidden="true" className="text-label-tertiary" />
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
              <ul className="card overflow-hidden [&>li+li]:shadow-[inset_0_0.5px_0_var(--separator)]">
                {data.transactions.map((t) => (
                  <li key={t.id} className="flex items-center gap-3 px-4 py-2.5">
                    <CategoryGlyph groupId={t.groupId} type={t.type} size={30} />
                    <div className="min-w-0 flex-1">
                      <p className="truncate text-[0.875rem]">{t.merchant}</p>
                      <p className="caption truncate">
                        {formatDate(t.date, dateFormat)} · {t.type === 'transfer' ? 'Transfer' : t.categoryName}
                      </p>
                    </div>
                    <p className={`tabular text-[0.875rem] ${t.type === 'income' ? 'text-positive' : ''}`}>{formatMoney(t.amount, t.currency, { signed: t.amount > 0 })}</p>
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
