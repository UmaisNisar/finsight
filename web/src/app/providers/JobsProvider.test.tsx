import { QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Job } from '@/api/schemas';
import { json, mockApi, testQueryClient } from '@/test/utils';
import { JOB_AUTO_DISMISS_MS, JOB_POLL_MS, JobsProvider, useJobs } from './JobsProvider';
import { ToastProvider } from './ToastProvider';

function job(status: Job['status'], overrides: Partial<Job> = {}): Job {
  return {
    id: 'job-1',
    kind: 'process',
    status,
    steps: [{ key: 'read', label: 'Reading statements', status: status === 'succeeded' ? 'done' : 'running', detail: null }],
    errorCode: null,
    errorMessage: null,
    createdAt: '2026-09-16T10:00:00Z',
    completedAt: null,
    ...overrides,
  };
}

function Probe() {
  const { job: current, isActive } = useJobs();
  return <p data-testid="probe">{current ? `${current.status}${isActive ? ' active' : ''}` : 'none'}</p>;
}

function setup(responses: (Job | Response)[]) {
  let index = 0;
  const fetchSpy = mockApi({
    '/api/jobs/active': job('running'),
    '/api/jobs/': () => {
      const next = responses[Math.min(index, responses.length - 1)];
      index++;
      return next;
    },
  });
  const client = testQueryClient();
  const invalidate = vi.spyOn(client, 'invalidateQueries');
  render(
    <QueryClientProvider client={client}>
      <ToastProvider>
        <JobsProvider enabled>
          <Probe />
        </JobsProvider>
      </ToastProvider>
    </QueryClientProvider>,
  );
  const jobRequests = () => fetchSpy.mock.calls.filter(([input]) => String(input) === '/api/jobs/job-1').length;
  return { jobRequests, invalidate };
}

const probe = () => screen.getByTestId('probe').textContent;
const advance = (ms: number) => act(() => vi.advanceTimersByTimeAsync(ms));
/** Response bodies are read outside the faked timers, so wait (in real time) for the UI to catch up. */
const expectProbe = (text: string) => vi.waitFor(() => expect(probe()).toBe(text));

beforeEach(() => vi.useFakeTimers({ shouldAdvanceTime: false }));
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('JobsProvider', () => {
  it('resumes an active job, polls until it succeeds, refreshes data and then stops', async () => {
    const { jobRequests, invalidate } = setup([job('running'), job('running'), job('succeeded')]);
    await advance(0);
    await expectProbe('running active');
    expect(jobRequests()).toBe(1);

    await advance(JOB_POLL_MS);
    expect(jobRequests()).toBe(2);
    await advance(JOB_POLL_MS);
    expect(jobRequests()).toBe(3);
    await expectProbe('succeeded');
    expect(screen.getByText('Your statements are ready')).toBeInTheDocument();
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['summary'] });

    await advance(JOB_POLL_MS * 5);
    expect(jobRequests()).toBe(3);

    // The finished checklist tucks itself away; the toast already confirmed it.
    await advance(JOB_AUTO_DISMISS_MS);
    await expectProbe('none');
  });

  it('keeps a failed job visible with its message', async () => {
    const { jobRequests } = setup([job('failed', { errorMessage: 'The PDF is password protected.' })]);
    await advance(0);
    await expectProbe('failed');
    expect(screen.getByRole('alert')).toHaveTextContent('The PDF is password protected.');

    await advance(JOB_AUTO_DISMISS_MS + JOB_POLL_MS * 3);
    await expectProbe('failed');
    expect(jobRequests()).toBe(1);
  });

  it('stops polling when the job can no longer be read', async () => {
    const { jobRequests } = setup([json({ code: 'not_found', message: 'That job no longer exists.' }, 404)]);
    await advance(0);
    const afterError = jobRequests();
    await advance(JOB_POLL_MS * 5);
    expect(jobRequests()).toBe(afterError);
    await expectProbe('none');
  });
});
