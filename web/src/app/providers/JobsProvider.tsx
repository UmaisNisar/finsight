import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { api } from '@/api/endpoints';
import { keys, useInvalidateFinancialData } from '@/api/queries';
import type { Job } from '@/api/schemas';
import { useToast } from './ToastProvider';

interface JobsContextValue {
  job: Job | null;
  isActive: boolean;
  track: (jobId: string) => void;
  dismiss: () => void;
}

const JobsContext = createContext<JobsContextValue>({ job: null, isActive: false, track: () => undefined, dismiss: () => undefined });

export function useJobs() {
  return useContext(JobsContext);
}

/**
 * Follows the background job (Gmail scan, statement processing, upload) the user started, polling its
 * persisted progress. Resumes an in-flight job after a reload, and refreshes data when it finishes.
 */
export function JobsProvider({ children, enabled }: { children: ReactNode; enabled: boolean }) {
  const [jobId, setJobId] = useState<string | null>(null);
  const [dismissed, setDismissed] = useState(false);
  const client = useQueryClient();
  const invalidate = useInvalidateFinancialData();
  const toast = useToast();
  const announced = useRef<string | null>(null);

  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    void api.activeJob().then((active) => {
      if (!cancelled && active) setJobId(active.id);
    });
    return () => {
      cancelled = true;
    };
  }, [enabled]);

  const query = useQuery({
    queryKey: ['job', jobId],
    queryFn: () => api.job(jobId ?? ''),
    enabled: enabled && jobId !== null,
    refetchInterval: (q) => {
      const status = q.state.data?.status;
      return status === 'succeeded' || status === 'failed' ? false : 900;
    },
  });

  const job = query.data ?? null;
  const isActive = job !== null && (job.status === 'queued' || job.status === 'running');

  useEffect(() => {
    if (!job || isActive || announced.current === job.id) return;
    announced.current = job.id;
    void invalidate();
    void client.invalidateQueries({ queryKey: keys.gmail });

    if (job.status === 'failed') {
      toast(job.errorMessage ?? 'That didn’t finish. Try again.', 'error');
    } else if (job.kind === 'sync') {
      toast('Gmail scan complete', 'success');
    } else {
      toast('Your statements are ready', 'success');
    }
  }, [job, isActive, invalidate, client, toast]);

  const track = useCallback((id: string) => {
    setDismissed(false);
    setJobId(id);
  }, []);

  const dismiss = useCallback(() => setDismissed(true), []);

  const value = useMemo<JobsContextValue>(
    () => ({ job: dismissed ? null : job, isActive, track, dismiss }),
    [job, dismissed, isActive, track, dismiss],
  );

  return <JobsContext.Provider value={value}>{children}</JobsContext.Provider>;
}
