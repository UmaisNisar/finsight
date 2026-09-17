import { type QueryClient, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { ApiError, errorMessage } from '@/api/client';
import { api } from '@/api/endpoints';
import { keys, useInvalidateFinancialData } from '@/api/queries';
import type { Job } from '@/api/schemas';
import { isStatementFile, needsPassword, UNSUPPORTED_FILE_MESSAGE } from '@/lib/statements';
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
  /** Why it failed, as a stable code (`pdf_password_protected`, `csv_unrecognized`), when the server gave one. */
  failureCode: string | null;
  /** It failed for want of a PDF password (or while being unlocked), so `unlockUpload` can send its held file again with one. */
  canUnlock: boolean;
}

export interface UploadOptions {
  scope: string;
  /** The statement this file fulfils, such as a statement alert. Without it the server matches the file itself. */
  statementId?: string;
  /** Called as the server accepts each file, with the statement it created or fulfilled. */
  onAccepted?: (statementId: string) => void;
  /** Opens password-protected PDFs. Sent with the upload request only; never stored or kept with the file. */
  password?: string;
}

export { UNSUPPORTED_FILE_MESSAGE };

interface UploadRecord {
  id: string;
  scope: string;
  name: string;
  phase: 'queued' | 'uploading' | 'sent' | 'failed';
  error: string | null;
  /** The code of an error from the upload request itself. Job failures carry theirs in the job. */
  code: string | null;
  /** The file has been sent again with a password, so a failure (even a network one) still offers the password field. */
  unlockAttempted: boolean;
  statementId: string | null;
  jobId: string | null;
}

/** A file kept in memory while its upload can still be retried with a password, and the statement it was sent for. */
interface HeldFile {
  file: File;
  target: string | undefined;
  statementId: string | null;
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
   * Uploads statement files one after another (never in parallel, across every caller) and follows each file's job
   * until the server has read it. Files FinSight can't read are rejected on the spot. Resolves once these files have
   * been sent.
   */
  uploadFiles: (files: File[], options: UploadOptions) => Promise<void>;
  /**
   * Sends a file that failed because it is a password-protected PDF again, with its password, in the same row. The
   * password goes into that one request and nowhere else. Resolves once the file has been sent.
   */
  unlockUpload: (id: string, password: string) => Promise<void>;
  /** The file uploaded this visit for a statement, while it is still held (a locked PDF waiting for its password). */
  heldFileFor: (statementId: string) => File | null;
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
  unlockUpload: async () => undefined,
  heldFileFor: () => null,
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
function uploadState(record: UploadRecord, job: Job | undefined, jobError: unknown): Pick<UploadItem, 'state' | 'message' | 'failureCode'> {
  if (record.phase !== 'sent') return { state: record.phase, message: record.error, failureCode: record.code };
  if (jobError) return { state: 'failed', message: errorMessage(jobError), failureCode: null };
  if (!job || !jobSettled(job)) return { state: 'processing', message: null, failureCode: null };
  if (job.status === 'failed') return { state: 'failed', message: job.errorMessage ?? UNREADABLE, failureCode: job.errorCode };
  const failedStep = job.steps.find((step) => step.status === 'failed');
  return failedStep ? { state: 'failed', message: failedStep.detail ?? UNREADABLE, failureCode: failedStep.code ?? null } : { state: 'done', message: null, failureCode: null };
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
 * It also runs the upload queue: statement files added anywhere are sent one at a time, and each file's own job is
 * followed so the screen that added it can show per-file progress. A password-protected PDF's file is held in memory
 * (never stored) until it is unlocked or removed, so it can be sent again with its password; passwords themselves are
 * only ever passed straight into the upload request.
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
  const held = useRef(new Map<string, HeldFile>());
  const patchRecord = useCallback((id: string, patch: Partial<UploadRecord>) => setRecords((list) => list.map((r) => (r.id === id ? { ...r, ...patch } : r))), []);

  /** Queues one file's request. The password, if any, lives only in this closure until the request is sent. */
  const send = useCallback(
    (id: string, file: File, statementId: string | undefined, password: string | undefined, onAccepted?: (statementId: string) => void) => {
      queue.current = queue.current.then(async () => {
        patchRecord(id, { phase: 'uploading' });
        try {
          const started = await api.uploadStatement(file, statementId, password);
          const entry = held.current.get(id);
          if (entry) entry.statementId = started.statementId;
          patchRecord(id, { phase: 'sent', jobId: started.jobId, statementId: started.statementId });
          onAccepted?.(started.statementId);
          // The fulfilled alert, or the new statement, changes status straight away.
          void client.invalidateQueries({ queryKey: keys.statements });
        } catch (error) {
          patchRecord(id, { phase: 'failed', error: errorMessage(error), code: error instanceof ApiError ? error.code : null });
        }
      });
      return queue.current;
    },
    [client, patchRecord],
  );

  const uploadFiles = useCallback(
    (files: File[], { scope, statementId, onAccepted, password }: UploadOptions) => {
      const added = files.map((file) => {
        const readable = isStatementFile(file);
        const record: UploadRecord = {
          id: `upload-${++uploadCounter}`,
          scope,
          name: file.name,
          phase: readable ? 'queued' : 'failed',
          error: readable ? null : UNSUPPORTED_FILE_MESSAGE,
          code: readable ? null : 'unsupported_file',
          unlockAttempted: false,
          statementId: null,
          jobId: null,
        };
        return { file, record };
      });
      // A new batch replaces the scope's finished files, so its list shows what is happening now.
      setRecords((list) => [...list.filter((r) => r.scope !== scope || !isSettledRecord(client, r)), ...added.map((a) => a.record)]);

      for (const { file, record } of added) {
        if (record.phase !== 'queued') continue;
        held.current.set(record.id, { file, target: statementId, statementId: statementId ?? null });
        send(record.id, file, statementId, password, onAccepted);
      }
      return queue.current;
    },
    [client, send],
  );

  const unlockUpload = useCallback(
    (id: string, password: string) => {
      const entry = held.current.get(id);
      if (!entry) return Promise.resolve();
      patchRecord(id, { phase: 'queued', error: null, code: null, jobId: null, unlockAttempted: true });
      // The same statement again: the one the server created or matched for this file.
      return send(id, entry.file, entry.statementId ?? entry.target, password);
    },
    [patchRecord, send],
  );

  const heldFileFor = useCallback((statementId: string) => {
    for (const entry of held.current.values()) {
      if (entry.statementId === statementId) return entry.file;
    }
    return null;
  }, []);

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
    const state = uploadState(record, result?.data, result?.error);
    // Files are held for as long as their rows are listed (until read or removed), so a locked one can always be sent again.
    const canUnlock = state.state === 'failed' && (needsPassword(state.failureCode) || record.unlockAttempted);
    return { id: record.id, scope: record.scope, name: record.name, statementId: record.statementId, jobId: record.jobId, ...state, canUnlock };
  });
  // Query results are new objects on every render; keep the list stable while nothing in it has changed.
  const derivedJson = JSON.stringify(derived);
  const uploads = useMemo(() => JSON.parse(derivedJson) as UploadItem[], [derivedJson]);

  // A file is held only while its row may still need it: once it has been read, or its row is gone, let it go.
  useEffect(() => {
    for (const id of held.current.keys()) {
      const item = uploads.find((upload) => upload.id === id);
      if (!item || item.state === 'done') held.current.delete(id);
    }
  }, [uploads]);

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
    () => ({ job: job && job.id !== dismissedId ? job : null, isActive, isRefreshing, isLoading, ready, track, dismiss, uploads, uploadFiles, unlockUpload, heldFileFor, clearUploads }),
    [job, dismissedId, isActive, isRefreshing, isLoading, ready, track, dismiss, uploads, uploadFiles, unlockUpload, heldFileFor, clearUploads],
  );

  return <JobsContext.Provider value={value}>{children}</JobsContext.Provider>;
}
