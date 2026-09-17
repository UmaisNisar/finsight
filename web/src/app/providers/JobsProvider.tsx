import { type QueryClient, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { errorMessage } from '@/api/client';
import { api } from '@/api/endpoints';
import { keys, useInvalidateFinancialData } from '@/api/queries';
import type { Job } from '@/api/schemas';
import { isPdfFile } from '@/lib/statements';
import { useToast } from './ToastProvider';

/** One file the user added: waiting its turn, being sent, being read by the server, or finished. */
export type UploadState = 'queued' | 'uploading' | 'processing' | 'done' | 'failed';

export interface UploadItem {
  id: string;
  /** Who started it (an alert card, the Statements page, onboarding), so each shows only its own files. */
  scope: string;
  name: string;
  state: UploadState;
  message: string | null;
  statementId: string | null;
  jobId: string | null;
}

export interface UploadOptions {
  scope: string;
  /** The statement this file fulfils, such as a statement alert. Without it the server matches the file itself. */
  statementId?: string;
  /** Called as the server accepts each file, with the statement it created or fulfilled. */
  onAccepted?: (statementId: string) => void;
}

export const NOT_A_PDF_MESSAGE = 'This isn’t a PDF. Download the statement as a PDF from your bank.';

interface UploadRecord {
  id: string;
  scope: string;
  name: string;
  phase: 'queued' | 'uploading' | 'sent' | 'failed';
  error: string | null;
  statementId: string | null;
  jobId: string | null;
}

interface JobsContextValue {
  job: Job | null;
  isActive: boolean;
  /** The job has finished but the data it changed is still being refetched. */
  isRefreshing: boolean;
  /** A job was just tracked and its first progress hasn't loaded yet. */
  isLoading: boolean;
  /** The check for a job left running by an earlier visit has finished. */
  ready: boolean;
  track: (jobId: string) => void;
  dismiss: () => void;
  /** Files added during this visit, oldest first, with live progress. */
  uploads: UploadItem[];
  /**
   * Uploads PDFs one after another (never in parallel, across every caller) and follows each file's job until the
   * server has read it. Files that aren't PDFs are rejected on the spot. Resolves once these files have been sent.
   */
  uploadFiles: (files: File[], options: UploadOptions) => Promise<void>;
  /** Removes finished files from a scope's list: one file, or all of them. Files still in flight stay. */
  clearUploads: (scope: string, id?: string) => void;
}

const JobsContext = createContext<JobsContextValue>({
  job: null,
  isActive: false,
  isRefreshing: false,
  isLoading: false,
  ready: false,
  track: () => undefined,
  dismiss: () => undefined,
  uploads: [],
  uploadFiles: async () => undefined,
  clearUploads: () => undefined,
});

export function useJobs() {
  return useContext(JobsContext);
}

export const JOB_POLL_MS = 900;
/** How long a successful job's checklist stays up before tucking itself away; the toast already confirmed it. */
export const JOB_AUTO_DISMISS_MS = 4000;

const jobSettled = (job: Job | undefined) => job?.status === 'succeeded' || job?.status === 'failed';
const UNREADABLE = 'This statement couldn’t be read.';

/** Where an upload stands, from its record and, once sent, its job. A file the server couldn't read fails its step. */
function uploadState(record: UploadRecord, job: Job | undefined, jobError: unknown): Pick<UploadItem, 'state' | 'message'> {
  if (record.phase !== 'sent') return { state: record.phase, message: record.error };
  if (jobError) return { state: 'failed', message: errorMessage(jobError) };
  if (!job || !jobSettled(job)) return { state: 'processing', message: null };
  if (job.status === 'failed') return { state: 'failed', message: job.errorMessage ?? UNREADABLE };
  const failedStep = job.steps.find((step) => step.status === 'failed');
  return failedStep ? { state: 'failed', message: failedStep.detail ?? UNREADABLE } : { state: 'done', message: null };
}

function isSettledRecord(client: QueryClient, record: UploadRecord): boolean {
  if (record.phase === 'failed') return true;
  if (record.phase !== 'sent' || !record.jobId) return false;
  const query = client.getQueryState<Job>(['job', record.jobId]);
  return query?.status === 'error' || jobSettled(query?.data);
}

let uploadCounter = 0;

/**
 * Follows the background job (Gmail scan, statement processing, upload) the user started, polling its
 * persisted progress. Resumes an in-flight job after a reload, and refreshes data when it finishes.
 * `quiet` is for screens that show progress inline (onboarding): no toasts, and finished jobs stay until dismissed.
 *
 * It also runs the upload queue: PDFs added anywhere are sent one at a time, and each file's own job is followed
 * so the screen that added it can show per-file progress.
 */
export function JobsProvider({ children, enabled, quiet = false }: { children: ReactNode; enabled: boolean; quiet?: boolean }) {
  const [jobId, setJobId] = useState<string | null>(null);
  const [dismissedId, setDismissedId] = useState<string | null>(null);
  const [refreshedId, setRefreshedId] = useState<string | null>(null);
  const [ready, setReady] = useState(false);
  const client = useQueryClient();
  const invalidate = useInvalidateFinancialData();
  const toast = useToast();
  const announced = useRef<string | null>(null);

  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    void api
      .activeJob()
      .catch(() => null)
      .then((active) => {
        if (cancelled) return;
        if (active) setJobId((current) => current ?? active.id);
        setReady(true);
      });
    return () => {
      cancelled = true;
    };
  }, [enabled]);

  const query = useQuery({
    queryKey: ['job', jobId],
    queryFn: () => api.job(jobId ?? ''),
    enabled: enabled && jobId !== null,
    // Poll until the job settles; stop if it can't be read (for example, it no longer exists).
    refetchInterval: (q) => {
      const status = q.state.data?.status;
      return q.state.status === 'error' || status === 'succeeded' || status === 'failed' ? false : JOB_POLL_MS;
    },
  });

  const job = query.data ?? null;
  const isActive = job !== null && (job.status === 'queued' || job.status === 'running');

  useEffect(() => {
    if (!job || isActive || announced.current === job.id) return;
    announced.current = job.id;
    const id = job.id;
    void Promise.all([invalidate(), client.invalidateQueries({ queryKey: keys.gmail })]).finally(() => setRefreshedId(id));

    if (quiet) {
      return;
    } else if (job.status === 'failed') {
      toast(job.errorMessage ?? 'That didn’t finish. Try again.', 'error');
    } else if (job.kind === 'sync') {
      toast('Gmail scan complete', 'success');
    } else {
      toast('Your statements are ready', 'success');
    }
  }, [job, isActive, invalidate, client, toast, quiet]);

  // Failures stay until dismissed, so their explanation can be read.
  const succeededId = job?.status === 'succeeded' ? job.id : null;
  useEffect(() => {
    if (!succeededId || quiet) return;
    const timer = window.setTimeout(() => setDismissedId(succeededId), JOB_AUTO_DISMISS_MS);
    return () => window.clearTimeout(timer);
  }, [succeededId, quiet]);

  const track = useCallback((id: string) => setJobId(id), []);
  const dismiss = useCallback(() => setDismissedId(jobId), [jobId]);

  // --- Uploads ---------------------------------------------------------------------------------------------------
  const [records, setRecords] = useState<UploadRecord[]>([]);
  const queue = useRef<Promise<void>>(Promise.resolve());
  const patchRecord = useCallback((id: string, patch: Partial<UploadRecord>) => setRecords((list) => list.map((r) => (r.id === id ? { ...r, ...patch } : r))), []);

  const uploadFiles = useCallback(
    (files: File[], { scope, statementId, onAccepted }: UploadOptions) => {
      const added = files.map((file) => {
        const pdf = isPdfFile(file);
        const record: UploadRecord = { id: `upload-${++uploadCounter}`, scope, name: file.name, phase: pdf ? 'queued' : 'failed', error: pdf ? null : NOT_A_PDF_MESSAGE, statementId: null, jobId: null };
        return { file, record };
      });
      // A new batch replaces the scope's finished files, so its list shows what is happening now.
      setRecords((list) => [...list.filter((r) => r.scope !== scope || !isSettledRecord(client, r)), ...added.map((a) => a.record)]);

      for (const { file, record } of added) {
        if (record.phase !== 'queued') continue;
        queue.current = queue.current.then(async () => {
          patchRecord(record.id, { phase: 'uploading' });
          try {
            const started = await api.uploadStatement(file, statementId);
            patchRecord(record.id, { phase: 'sent', jobId: started.jobId, statementId: started.statementId });
            onAccepted?.(started.statementId);
            // The fulfilled alert, or the new statement, changes status straight away.
            void client.invalidateQueries({ queryKey: keys.statements });
          } catch (error) {
            patchRecord(record.id, { phase: 'failed', error: errorMessage(error) });
          }
        });
      }
      return queue.current;
    },
    [client, patchRecord],
  );

  const clearUploads = useCallback(
    (scope: string, id?: string) => setRecords((list) => list.filter((r) => r.scope !== scope || (id !== undefined && r.id !== id) || !isSettledRecord(client, r))),
    [client],
  );

  const uploadJobIds = [...new Set(records.flatMap((r) => (r.phase === 'sent' && r.jobId ? [r.jobId] : [])))];
  const uploadJobs = useQueries({
    queries: uploadJobIds.map((id) => ({
      queryKey: ['job', id],
      queryFn: () => api.job(id),
      refetchInterval: (q: { state: { status: string; data?: Job } }) => (q.state.status === 'error' || jobSettled(q.state.data) ? false : JOB_POLL_MS),
    })),
  });

  const derived = records.map((record): UploadItem => {
    const result = record.jobId ? uploadJobs[uploadJobIds.indexOf(record.jobId)] : undefined;
    return { id: record.id, scope: record.scope, name: record.name, statementId: record.statementId, jobId: record.jobId, ...uploadState(record, result?.data, result?.error) };
  });
  // Query results are new objects on every render; keep the list stable while nothing in it has changed.
  const derivedJson = JSON.stringify(derived);
  const uploads = useMemo(() => JSON.parse(derivedJson) as UploadItem[], [derivedJson]);

  // As each file's job settles, refresh what it changed: the statement, and the figures built from it.
  const refreshedUploads = useRef(new Set<string>());
  const settledKey = uploadJobIds.filter((_id, index) => uploadJobs[index]?.isError || jobSettled(uploadJobs[index]?.data)).join(',');
  useEffect(() => {
    const fresh = settledKey ? settledKey.split(',').filter((id) => !refreshedUploads.current.has(id)) : [];
    if (fresh.length === 0) return;
    for (const id of fresh) refreshedUploads.current.add(id);
    void invalidate();
  }, [settledKey, invalidate]);

  const isRefreshing = job !== null && !isActive && refreshedId !== job.id;
  const isLoading = enabled && jobId !== null && query.isPending && !query.isError;
  const value = useMemo<JobsContextValue>(
    () => ({ job: job && job.id !== dismissedId ? job : null, isActive, isRefreshing, isLoading, ready, track, dismiss, uploads, uploadFiles, clearUploads }),
    [job, dismissedId, isActive, isRefreshing, isLoading, ready, track, dismiss, uploads, uploadFiles, clearUploads],
  );

  return <JobsContext.Provider value={value}>{children}</JobsContext.Provider>;
}
