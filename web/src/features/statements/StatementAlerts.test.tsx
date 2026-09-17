import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Job, Statement } from '@/api/schemas';
import { JobsProvider, UNSUPPORTED_FILE_MESSAGE } from '@/app/providers/JobsProvider';
import { alertGuidance, alertReadyLabel, GENERIC_DOWNLOAD_HINT, groupAlerts, isPdfFile, SEVERAL_PDFS_HINT } from '@/lib/statements';
import { alert, CIBC_HINT } from '@/test/alerts';
import { deferred, json, mockApi, renderWithApp } from '@/test/utils';
import { StatementAlertList } from './StatementAlerts';

afterEach(() => vi.restoreAllMocks());

const WAIT = { timeout: 4000 };
const september = alert('sep', '2026-09-15T13:00:00Z');
const cibc = [september, alert('jul', '2026-07-15T13:00:00Z'), alert('aug', '2026-08-15T13:00:00Z')];
const tangerine = alert('tng', '2026-09-03T09:00:00Z', { title: 'Tangerine chequing account ending 1020', institution: 'Tangerine', senderName: 'Tangerine', accountType: 'chequing', accountMask: '1020', signInUrl: null, downloadHint: null });

const pdf = (name: string) => new File(['%PDF-1.7'], name, { type: 'application/pdf' });

function job(id: string, overrides: Partial<Job> = {}): Job {
  return {
    id,
    kind: 'upload',
    status: 'succeeded',
    steps: [{ key: 'read', label: 'Reading statement', status: 'done', detail: '42 transactions' }],
    errorCode: null,
    errorMessage: null,
    createdAt: '2026-09-16T10:00:00Z',
    completedAt: '2026-09-16T10:00:10Z',
    ...overrides,
  };
}

function renderAlerts(statements: Statement[], handlers: Record<string, unknown> = {}) {
  const fetchSpy = mockApi({ '/api/jobs/active': () => new Response(null, { status: 204 }), '/api/statements': statements, ...handlers });
  const view = renderWithApp(
    <JobsProvider enabled quiet>
      <StatementAlertList statements={statements} dateFormat="MMM d, yyyy" />
    </JobsProvider>,
  );
  const uploads = () => fetchSpy.mock.calls.filter(([input, init]) => String(input) === '/api/uploads/statements' && init?.method === 'POST').map(([, init]) => init?.body as FormData);
  const card = (title: string) => screen.getByRole('heading', { level: 3, name: title }).closest('section') as HTMLElement;
  const pick = (target: HTMLElement, files: File[]) => {
    const input = target.querySelector('input[type="file"]') as HTMLInputElement;
    act(() => fireEvent.change(input, { target: { files } }));
  };
  const rows = (target: HTMLElement) => within(target).queryAllByRole('listitem').map((li) => `${li.querySelector('.truncate')?.textContent}:${li.dataset.state}`);
  return { ...view, fetchSpy, uploads, card, pick, rows };
}

describe('statement alert helpers', () => {
  it('groups alerts by account, oldest first, and ignores everything else', () => {
    const groups = groupAlerts([...cibc, tangerine, alert('other', '2026-09-01T00:00:00Z', { accountMask: '1111', title: 'CIBC credit card ending 1111' }), { ...september, id: 'done', status: 'processed' }]);
    expect(groups.map((g) => [g.title, g.alerts.map((a) => a.id)])).toEqual([
      ['CIBC credit card ending 5190', ['jul', 'aug', 'sep']],
      ['Tangerine chequing account ending 1020', ['tng']],
      ['CIBC credit card ending 1111', ['other']],
    ]);
  });

  it('labels one alert by its date and several by count and months', () => {
    const labels = (statements: Statement[]) => groupAlerts(statements).map((group) => alertReadyLabel(group, 'MMM d, yyyy'));
    expect(labels([...cibc, tangerine])).toEqual(['3 statements ready · Jul, Aug, Sep', 'Statement ready Sep 3']);
    expect(labels(['2026-03', '2026-04', '2026-05', '2026-06', '2026-07', '2026-08'].map((m) => alert(m, `${m}-15T12:00:00Z`)))).toEqual(['6 statements ready · Mar – Aug']);
  });

  it('guides with the bank’s own hint, a generic one otherwise, and says several PDFs are fine', () => {
    expect(groupAlerts([...cibc, tangerine]).map(alertGuidance)).toEqual([`${CIBC_HINT} ${SEVERAL_PDFS_HINT}`, GENERIC_DOWNLOAD_HINT]);
  });

  it('recognises PDFs by type, or by name when the browser gives no type', () => {
    expect(isPdfFile(pdf('a.pdf'))).toBe(true);
    expect(isPdfFile(new File([''], 'Statement.PDF'))).toBe(true);
    expect(isPdfFile(new File([''], 'photo.png', { type: 'image/png' }))).toBe(false);
    expect(isPdfFile(new File([''], 'notes.txt'))).toBe(false);
  });
});

describe('Statement alert cards', () => {
  it('shows each account once, with its months, guidance and a sign-in link that opens safely', () => {
    const { card } = renderAlerts([...cibc, tangerine]);
    const cibcCard = card('CIBC credit card ending 5190');
    expect(cibcCard).toHaveTextContent('3 statements ready · Jul, Aug, Sep');
    expect(cibcCard).toHaveTextContent(SEVERAL_PDFS_HINT);
    const link = within(cibcCard).getByRole('link', { name: /Open CIBC/ });
    expect(link).toHaveAttribute('href', september.signInUrl);
    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    expect(within(cibcCard).getByRole('button', { name: 'Upload statements' })).toBeInTheDocument();

    const single = card('Tangerine chequing account ending 1020');
    expect(single).toHaveTextContent('Statement ready Sep 3');
    expect(single).toHaveTextContent(GENERIC_DOWNLOAD_HINT);
    expect(single).not.toHaveTextContent(SEVERAL_PDFS_HINT);
    expect(within(single).queryByRole('link')).not.toBeInTheDocument();
    expect(within(single).getByRole('button', { name: 'Upload statement' })).toBeInTheDocument();
  });

  it('uploads into the alert itself when the account has one statement waiting', async () => {
    const { card, pick, uploads } = renderAlerts([tangerine], { 'POST /api/uploads/statements': { jobId: 'j1', statementId: 'tng' }, '/api/jobs/': job('j1') });
    pick(card('Tangerine chequing account ending 1020'), [pdf('tangerine-sep.pdf')]);
    await waitFor(() => expect(uploads()).toHaveLength(1));
    expect(uploads()[0]?.get('statementId')).toBe('tng');
    expect((uploads()[0]?.get('file') as File).name).toBe('tangerine-sep.pdf');
  });

  it('lets the server match each file when several statements are waiting', async () => {
    let n = 0;
    const { card, pick, uploads } = renderAlerts(cibc, { 'POST /api/uploads/statements': () => ({ jobId: `j${++n}`, statementId: `s${n}` }), '/api/jobs/': ({ url }: { url: URL }) => job(url.pathname.split('/').pop() ?? '') });
    pick(card('CIBC credit card ending 5190'), [pdf('jul.pdf'), pdf('aug.pdf')]);
    await waitFor(() => expect(uploads()).toHaveLength(2), WAIT);
    expect(uploads().map((body) => body.get('statementId'))).toEqual([null, null]);
  });

  it('sends files one at a time and shows each one’s progress, including failures', async () => {
    const first = deferred<unknown>();
    const firstJob = deferred<unknown>();
    const responses = [() => first.promise, () => json({ code: 'rate_limited', message: 'Too many uploads right now. Try again in a few minutes.' }, 429), () => ({ jobId: 'j3', statementId: 's3' })];
    let n = 0;
    const { card, pick, uploads, rows } = renderAlerts(cibc, {
      'POST /api/uploads/statements': () => responses[n++]?.(),
      '/api/jobs/j1': () => firstJob.promise,
      '/api/jobs/j3': job('j3', { steps: [{ key: 'read', label: 'Reading statement', status: 'failed', detail: 'This PDF is password protected.' }] }),
    });
    const target = card('CIBC credit card ending 5190');
    pick(target, [pdf('jul.pdf'), pdf('aug.pdf'), pdf('sep.pdf')]);

    await waitFor(() => expect(rows(target)).toEqual(['jul.pdf:uploading', 'aug.pdf:queued', 'sep.pdf:queued']));
    expect(uploads()).toHaveLength(1);

    await act(async () => first.resolve({ jobId: 'j1', statementId: 's1' }));
    await waitFor(() => expect(rows(target)).toEqual(['jul.pdf:processing', 'aug.pdf:failed', 'sep.pdf:failed']), WAIT);
    expect(uploads()).toHaveLength(3);
    expect(target).toHaveTextContent('Too many uploads right now. Try again in a few minutes.');
    expect(target).toHaveTextContent('This PDF is password protected.');

    await act(async () => firstJob.resolve(job('j1')));
    await waitFor(() => expect(rows(target)).toEqual(['jul.pdf:done', 'aug.pdf:failed', 'sep.pdf:failed']), WAIT);

    // Failures stay until removed.
    act(() => fireEvent.click(within(target).getByRole('button', { name: 'Remove aug.pdf from the list' })));
    await waitFor(() => expect(rows(target)).toEqual(['jul.pdf:done', 'sep.pdf:failed']), WAIT);
  });

  it('rejects files that aren’t PDFs without sending them', async () => {
    const { card, pick, uploads, rows } = renderAlerts(cibc);
    const target = card('CIBC credit card ending 5190');
    pick(target, [new File(['x'], 'screenshot.png', { type: 'image/png' })]);
    await waitFor(() => expect(rows(target)).toEqual(['screenshot.png:failed']));
    expect(target).toHaveTextContent(UNSUPPORTED_FILE_MESSAGE);
    expect(uploads()).toHaveLength(0);
  });

  it('accepts PDFs dropped onto the card', async () => {
    const { card, uploads } = renderAlerts([tangerine], { 'POST /api/uploads/statements': { jobId: 'j1', statementId: 'tng' }, '/api/jobs/': job('j1') });
    const target = card('Tangerine chequing account ending 1020');
    const dataTransfer = { types: ['Files'], files: [pdf('dropped.pdf')], dropEffect: 'none' };
    act(() => fireEvent.dragEnter(target, { dataTransfer }));
    expect(within(target).getByText('Drop files to upload').parentElement).toHaveClass('opacity-100');
    act(() => fireEvent.drop(target, { dataTransfer }));
    expect(within(target).getByText('Drop files to upload').parentElement).toHaveClass('opacity-0');
    await waitFor(() => expect(uploads()).toHaveLength(1));
    expect((uploads()[0]?.get('file') as File).name).toBe('dropped.pdf');
  });

  it('dismisses every alert for the account after confirming', async () => {
    const { card, fetchSpy } = renderAlerts(cibc, { 'POST /api/statements/': () => new Response(null, { status: 204 }) });
    act(() => fireEvent.click(within(card('CIBC credit card ending 5190')).getByRole('button', { name: 'Dismiss' })));
    const dialog = await screen.findByRole('alertdialog', { hidden: true });
    expect(dialog).toHaveTextContent('isn’t yours or you already have these statements');
    act(() => fireEvent.click(within(dialog).getByRole('button', { name: 'Dismiss', hidden: true })));
    await waitFor(() => {
      const dismissed = fetchSpy.mock.calls.filter(([input, init]) => init?.method === 'POST' && String(input).endsWith('/dismiss')).map(([input]) => String(input));
      expect(dismissed.sort()).toEqual(['/api/statements/aug/dismiss', '/api/statements/jul/dismiss', '/api/statements/sep/dismiss']);
    });
  });
});
