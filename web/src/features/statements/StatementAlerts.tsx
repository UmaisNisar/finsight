import { useMutation, useQueryClient } from '@tanstack/react-query';
import { AnimatePresence, motion } from 'motion/react';
import { ArrowUpRight, FileUp } from 'lucide-react';
import { useEffect, useId, useState } from 'react';
import { errorMessage } from '@/api/client';
import { api } from '@/api/endpoints';
import { keys } from '@/api/queries';
import type { Statement } from '@/api/schemas';
import { useConfirm } from '@/app/providers/ConfirmProvider';
import { useJobs, type UploadItem } from '@/app/providers/JobsProvider';
import { DropHighlight, UploadProgressList, usePdfPicker, type UploadRowData } from '@/components/UploadProgressList';
import { Collapse } from '@/components/ui/AutoHeight';
import { Button, buttonStyles } from '@/components/ui/Button';
import { useFileDrop } from '@/hooks/useFileDrop';
import { useSingleFlight } from '@/hooks/useSingleFlight';
import { cn } from '@/lib/cn';
import { alertCountLabel, alertGuidance, alertMonths, alertReadyLabel, groupAlerts, type AlertGroup } from '@/lib/statements';

/** How long finished uploads stay listed (with their checks) before the list, or an emptied card, folds away. */
export const UPLOAD_LINGER_MS = 1800;

export const alertScope = (groupKey: string) => `alert:${groupKey}`;

const HEIGHT_SPRING = { type: 'spring', stiffness: 440, damping: 42 } as const;

/** Rows for a list of tracked uploads, each failed one removable. */
export function uploadRows(items: UploadItem[], onRemove: (id: string) => void, detail?: (item: UploadItem) => string | null): UploadRowData[] {
  return items.map((item) => ({ key: item.id, name: item.name, state: item.state, message: item.message, detail: detail?.(item) ?? null, onRemove: () => onRemove(item.id) }));
}

/**
 * Clears a scope's uploads a moment after every one of them has finished, so the checks are seen before the list
 * folds away. Failures keep the list open until they are removed.
 */
export function useClearWhenDone(scope: string, items: UploadItem[]) {
  const { clearUploads } = useJobs();
  const allDone = items.length > 0 && items.every((item) => item.state === 'done');
  useEffect(() => {
    if (!allDone) return;
    const timer = window.setTimeout(() => clearUploads(scope), UPLOAD_LINGER_MS);
    return () => window.clearTimeout(timer);
  }, [allDone, clearUploads, scope]);
}

/** A bank's monogram: short all-caps names (CIBC, RBC, TD) in full, anything else by its first letter. */
export function InstitutionBadge({ name, size = 40 }: { name: string | null; size?: number }) {
  const trimmed = (name ?? '').trim();
  const text = /^[A-Z&]{2,4}$/.test(trimmed) ? trimmed : (trimmed[0]?.toUpperCase() ?? '?');
  return (
    <span
      aria-hidden="true"
      style={{ width: size, height: size, fontSize: text.length > 2 ? size * 0.27 : text.length === 2 ? size * 0.34 : size * 0.42 }}
      className="flex shrink-0 items-center justify-center rounded-[12px] bg-fill-strong font-semibold tracking-[-0.02em] text-label-secondary"
    >
      {text}
    </span>
  );
}

/** "3 statements ready · Jul, Aug, Sep", where each month pops out of the line as its statement is uploaded. */
function ReadyLine({ group, dateFormat }: { group: AlertGroup; dateFormat: string }) {
  const months = alertMonths(group);
  const several = group.alerts.length > 1;
  if (!several || months.length === 0 || months.length > 4) {
    return <span key={alertReadyLabel(group, dateFormat)} className="fade-in">{alertReadyLabel(group, dateFormat)}</span>;
  }
  return (
    <>
      {alertCountLabel(group, dateFormat)}
      {' · '}
      <AnimatePresence mode="popLayout" initial={false}>
        {months.map((month, index) => (
          <motion.span
            key={month.id}
            layout="position"
            className="inline-block whitespace-pre"
            initial={{ opacity: 0, scale: 0.8 }}
            animate={{ opacity: 1, scale: 1 }}
            exit={{ opacity: 0, scale: 0.6, transition: { duration: 0.18 } }}
            transition={{ type: 'spring', stiffness: 500, damping: 34 }}
          >
            {index > 0 ? ', ' : ''}
            {month.label}
          </motion.span>
        ))}
      </AnimatePresence>
    </>
  );
}

interface CardProps {
  group: AlertGroup;
  dateFormat: string;
  /** Called with each statement the server accepts a PDF for. */
  onUploadAccepted?: (statementId: string) => void;
}

/**
 * One account's statement alerts: what's ready, how to get it from the bank, and a place to upload the PDFs
 * (a button or a drop). Upload progress shows inline; each uploaded month leaves the list, and the card folds away
 * once nothing is left to do.
 */
export function StatementAlertCard({ group, dateFormat, onUploadAccepted }: CardProps) {
  const jobs = useJobs();
  const client = useQueryClient();
  const confirm = useConfirm();
  const once = useSingleFlight();
  const titleId = useId();
  const scope = alertScope(group.key);
  const items = jobs.uploads.filter((item) => item.scope === scope);
  const inFlight = items.some((item) => item.state !== 'done' && item.state !== 'failed');
  const count = group.alerts.length;
  useClearWhenDone(scope, items);

  function upload(files: File[]) {
    // One alert and one file: that file is that statement. Otherwise the server matches each file by its period.
    const only = count === 1 && files.length === 1 ? group.alerts[0] : undefined;
    void jobs.uploadFiles(files, { scope, statementId: only?.id, onAccepted: onUploadAccepted });
  }

  const picker = usePdfPicker(upload);
  const drop = useFileDrop(upload, count === 0);

  const dismiss = useMutation({
    mutationFn: async (ids: string[]) => {
      await Promise.all(ids.map((id) => api.dismissStatement(id)));
    },
    onSuccess: (_result, ids) => {
      client.setQueryData<Statement[]>(keys.statements, (list) => list?.filter((s) => !ids.includes(s.id)));
      void client.invalidateQueries({ queryKey: keys.statements });
    },
  });

  async function askToDismiss() {
    const ok = await confirm({
      title: count === 1 ? 'Dismiss this statement?' : `Dismiss ${count} statements?`,
      message: `Choose this if ${group.title} isn’t yours or you already have ${count === 1 ? 'this statement' : 'these statements'}. FinSight won’t ask for ${count === 1 ? 'it' : 'them'} again.`,
      confirmLabel: 'Dismiss',
    });
    if (ok) await dismiss.mutateAsync(group.alerts.map((alert) => alert.id));
  }

  const institution = group.institution ?? 'your bank';

  return (
    <section aria-labelledby={titleId} className="card relative rounded-[22px]" data-testid="statement-alert" {...drop.handlers}>
      {picker.input}
      <div className="flex gap-3.5 p-4 md:p-5">
        <InstitutionBadge name={group.institution} />
        <div className="min-w-0 flex-1">
          <h3 id={titleId} className="text-[0.9375rem] leading-5 font-semibold tracking-[-0.01em]">
            {group.title}
          </h3>
          <p className="mt-0.5 text-[0.875rem] leading-5 text-label-secondary">
            {count > 0 ? (
              <ReadyLine group={group} dateFormat={dateFormat} />
            ) : (
              <span key={inFlight ? 'reading' : 'done'} className="fade-in">
                {inFlight ? 'Reading your statements…' : 'All statements uploaded'}
              </span>
            )}
          </p>

          {/* Once every statement for the account is uploaded, the instructions and buttons fold away. */}
          <Collapse open={count > 0}>
            <p className="mt-2 text-[0.8125rem] leading-snug text-label-secondary">{alertGuidance(group)}</p>

            <div className="mt-3.5 flex flex-wrap items-center gap-2">
              {group.signInUrl && (
                <a href={group.signInUrl} target="_blank" rel="noopener noreferrer" className={buttonStyles({ variant: 'secondary', size: 'sm' })}>
                  Open {institution}
                  <ArrowUpRight size={14} strokeWidth={2.25} aria-hidden="true" />
                  <span className="sr-only">(opens in a new tab)</span>
                </a>
              )}
              <Button variant="tinted" size="sm" icon={<FileUp size={14} aria-hidden="true" />} disabled={count === 0} onClick={picker.open}>
                {count === 1 ? 'Upload PDF' : 'Upload PDFs'}
              </Button>
              <Button
                variant="plain"
                size="sm"
                className="text-label-secondary hover:bg-fill sm:ml-auto sm:-mr-2"
                disabled={count === 0 || inFlight}
                loading={dismiss.isPending}
                onClick={() => void once(askToDismiss)}
              >
                Dismiss
              </Button>
            </div>
          </Collapse>

          {dismiss.isError && (
            <p role="alert" className="fade-in mt-2 text-[0.8125rem] text-critical">
              {errorMessage(dismiss.error)}
            </p>
          )}

          <Collapse open={items.length > 0}>
            <UploadProgressList className="mt-3.5" label={`Uploads for ${group.title}`} rows={uploadRows(items, (id) => jobs.clearUploads(scope, id))} />
          </Collapse>
        </div>
      </div>
      <DropHighlight active={drop.dragging} />
    </section>
  );
}

/**
 * Keeps a group that has just been emptied (its last alert uploaded) so its card can finish showing progress and
 * then fold away, instead of vanishing the moment the server marks the alert fulfilled. Adjusted during render,
 * like useRetained, so there's never a frame without the card.
 */
function useRetainedGroups(groups: AlertGroup[]): AlertGroup[] {
  const signature = groups.map((g) => `${g.key}:${g.alerts.map((a) => a.id).join(',')}`).join('|');
  const [state, setState] = useState({ signature, keys: groups.map((g) => g.key), snapshots: new Map(groups.map((g) => [g.key, g])) });
  if (state.signature !== signature) {
    const snapshots = new Map(state.snapshots);
    const order = [...state.keys];
    for (const group of groups) {
      snapshots.set(group.key, group);
      if (!order.includes(group.key)) order.push(group.key);
    }
    setState({ signature, keys: order, snapshots });
  }
  const current = new Map(groups.map((g) => [g.key, g]));
  return state.keys.flatMap((key) => {
    const group = current.get(key) ?? state.snapshots.get(key);
    return group ? [current.has(key) ? group : { ...group, alerts: [] }] : [];
  });
}

/** Every alert group from a statement list, as a stack of cards that fold in and out. */
export function StatementAlertList({ statements, dateFormat, onUploadAccepted, className }: { statements: Statement[]; dateFormat: string; onUploadAccepted?: (statementId: string) => void; className?: string }) {
  const jobs = useJobs();
  const groups = useRetainedGroups(groupAlerts(statements));
  const visible = groups.filter((group) => group.alerts.length > 0 || jobs.uploads.some((item) => item.scope === alertScope(group.key)));

  return (
    // Each card carries the gap below it inside its collapsing wrapper, so spacing folds away with the card.
    <div className={cn('-mb-3', className)}>
      <AnimatePresence initial={false}>
        {visible.map((group) => (
          <motion.div
            key={group.key}
            initial={{ height: 0, overflow: 'clip' }}
            animate={{ height: 'auto', transitionEnd: { overflow: 'visible' } }}
            exit={{ height: 0, overflow: 'clip' }}
            transition={HEIGHT_SPRING}
          >
            <div className="pb-3">
              <StatementAlertCard group={group} dateFormat={dateFormat} onUploadAccepted={onUploadAccepted} />
            </div>
          </motion.div>
        ))}
      </AnimatePresence>
    </div>
  );
}
