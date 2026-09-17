import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Job, StatementDetail as StatementDetailData } from '@/api/schemas';
import { JobsProvider, UNSUPPORTED_FILE_MESSAGE } from '@/app/providers/JobsProvider';
import { PASSWORD_HINT, WRONG_PASSWORD } from '@/components/PdfPasswordPrompt';
import { isStatementFile, STATEMENT_FILE_ACCEPT } from '@/lib/statements';
import { alert } from '@/test/alerts';
import { mockApi, renderWithApp } from '@/test/utils';
import { StatementAlertList } from './StatementAlerts';
import { StatementDetail } from './StatementDetail';

afterEach(() => vi.restoreAllMocks());

const WAIT = { timeout: 4000 };
const PASSWORD = 'Dob-19900101-secret';
const tangerine = alert('tng', '2026-09-03T09:00:00Z', { title: 'Tangerine chequing account ending 1020', institution: 'Tangerine', senderName: 'Tangerine', accountType: 'chequing', accountMask: '1020', signInUrl: null, downloadHint: null });

function job(id: string, step: Partial<Job['steps'][number]> = {}): Job {
  return {
    id,
    kind: 'upload',
    status: 'succeeded',
    steps: [{ key: 's:tng', label: 'Statement', status: 'done', detail: '3 transactions', code: null, ...step }],
    errorCode: null,
    errorMessage: null,
    createdAt: '2026-09-16T10:00:00Z',
    completedAt: '2026-09-16T10:00:10Z',
  };
}

const locked = (id: string, code: 'pdf_password_protected' | 'pdf_password_incorrect') =>
  job(id, { status: 'failed', detail: 'This PDF is password-protected. Enter its password to unlock it.', code });

function uploadBodies(fetchSpy: ReturnType<typeof mockApi>) {
  return fetchSpy.mock.calls.filter(([input, init]) => String(input) === '/api/uploads/statements' && init?.method === 'POST').map(([, init]) => init?.body as FormData);
}

/** Nothing about the password may leave the request body: no URL, no cached query or mutation, no storage. */
function expectPasswordNowhereButRequestBodies(fetchSpy: ReturnType<typeof mockApi>, client: import('@tanstack/react-query').QueryClient) {
  expect(fetchSpy.mock.calls.map(([input]) => String(input)).join('\n')).not.toContain(PASSWORD);
  const cached = JSON.stringify([
    client.getQueryCache().getAll().map((query) => [query.queryKey, query.state.data]),
    client.getMutationCache().getAll().map((mutation) => mutation.state.variables),
  ]);
  expect(cached).not.toContain(PASSWORD);
  expect(JSON.stringify({ ...window.localStorage })).not.toContain(PASSWORD);
  expect(JSON.stringify({ ...window.sessionStorage })).not.toContain(PASSWORD);
}

describe('statement file types', () => {
  it('pickers offer PDF, CSV, OFX and QFX, and only those are sent', async () => {
    expect(STATEMENT_FILE_ACCEPT.split(',')).toEqual(expect.arrayContaining(['.pdf', '.csv', '.ofx', '.qfx']));
    expect(isStatementFile(new File([''], 'Aug.CSV', { type: 'application/vnd.ms-excel' }))).toBe(true);
    expect(isStatementFile(new File([''], 'download.qfx'))).toBe(true);
    expect(isStatementFile(new File([''], 'download.ofx', { type: 'application/x-ofx' }))).toBe(true);
    expect(isStatementFile(new File([''], 'statement.pdf', { type: 'application/pdf' }))).toBe(true);
    expect(isStatementFile(new File([''], 'export.xlsx', { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' }))).toBe(false);

    let n = 0;
    const fetchSpy = mockApi({
      '/api/jobs/active': () => new Response(null, { status: 204 }),
      '/api/statements': [tangerine],
      'POST /api/uploads/statements': () => ({ jobId: `j${++n}`, statementId: `s${n}` }),
      '/api/jobs/': ({ url }: { url: URL }) => job(url.pathname.split('/').pop() ?? ''),
    });
    renderWithApp(
      <JobsProvider enabled quiet>
        <StatementAlertList statements={[tangerine]} dateFormat="MMM d, yyyy" />
      </JobsProvider>,
    );
    const card = screen.getByRole('heading', { level: 3, name: tangerine.title }).closest('section') as HTMLElement;
    const input = card.querySelector('input[type="file"]') as HTMLInputElement;
    expect(input).toHaveAttribute('accept', STATEMENT_FILE_ACCEPT);

    act(() =>
      fireEvent.change(input, {
        target: { files: [new File(['a,b'], 'aug.csv', { type: 'text/csv' }), new File(['<OFX>'], 'aug.qfx'), new File(['PK'], 'aug.xlsx', { type: 'application/zip' })] },
      }),
    );

    await waitFor(() => expect(uploadBodies(fetchSpy)).toHaveLength(2), WAIT);
    expect(uploadBodies(fetchSpy).map((body) => (body.get('file') as File).name)).toEqual(['aug.csv', 'aug.qfx']);
    expect(card).toHaveTextContent(UNSUPPORTED_FILE_MESSAGE);
  });
});

describe('password-protected PDFs', () => {
  it('asks for the password in the upload row and sends the same file again with it', async () => {
    const jobs: Record<string, Job> = { j1: locked('j1', 'pdf_password_protected'), j2: locked('j2', 'pdf_password_incorrect'), j3: job('j3') };
    let n = 0;
    const fetchSpy = mockApi({
      '/api/jobs/active': () => new Response(null, { status: 204 }),
      '/api/statements': [tangerine],
      'POST /api/uploads/statements': () => ({ jobId: `j${++n}`, statementId: 'locked-1' }),
      '/api/jobs/': ({ url }: { url: URL }) => jobs[url.pathname.split('/').pop() ?? ''],
    });
    const { client } = renderWithApp(
      <JobsProvider enabled quiet>
        <StatementAlertList statements={[tangerine]} dateFormat="MMM d, yyyy" />
      </JobsProvider>,
    );
    const card = screen.getByRole('heading', { level: 3, name: tangerine.title }).closest('section') as HTMLElement;
    const file = new File(['%PDF-1.4 encrypted'], 'tangerine-sep.pdf', { type: 'application/pdf' });
    act(() => fireEvent.change(card.querySelector('input[type="file"]') as HTMLInputElement, { target: { files: [file] } }));

    const field = await within(card).findByLabelText('PDF password', {}, WAIT);
    expect(card).toHaveTextContent(PASSWORD_HINT);
    expect(field).toHaveAttribute('type', 'password');
    const unlock = within(card).getByRole('button', { name: 'Unlock' });
    expect(unlock).toBeDisabled();

    act(() => fireEvent.click(within(card).getByRole('button', { name: 'Show password' })));
    expect(field).toHaveAttribute('type', 'text');
    act(() => fireEvent.click(within(card).getByRole('button', { name: 'Hide password' })));
    expect(field).toHaveAttribute('type', 'password');

    // A wrong password: the same file goes again, to the statement the first upload created.
    act(() => fireEvent.change(field, { target: { value: 'wrong-guess' } }));
    act(() => fireEvent.click(unlock));
    await waitFor(() => expect(uploadBodies(fetchSpy)).toHaveLength(2), WAIT);
    const retry = uploadBodies(fetchSpy)[1];
    expect(retry?.get('file')).toBe(file);
    expect(retry?.get('statementId')).toBe('locked-1');
    expect(retry?.get('password')).toBe('wrong-guess');
    await within(card).findByText(WRONG_PASSWORD, {}, WAIT);
    expect((within(card).getByLabelText('PDF password') as HTMLInputElement).value).toBe('');

    // The right one unlocks it, and the row finishes in place.
    const again = within(card).getByLabelText('PDF password');
    act(() => fireEvent.change(again, { target: { value: PASSWORD } }));
    act(() => fireEvent.click(within(card).getByRole('button', { name: 'Unlock' })));
    await waitFor(() => expect(uploadBodies(fetchSpy)).toHaveLength(3), WAIT);
    expect(uploadBodies(fetchSpy)[2]?.get('password')).toBe(PASSWORD);
    expect(uploadBodies(fetchSpy)[2]?.get('file')).toBe(file);
    await waitFor(() => expect(within(card).getAllByRole('listitem').map((li) => li.dataset.state)).toEqual(['done']), WAIT);

    expect(uploadBodies(fetchSpy)[0]?.get('password')).toBeNull();
    expectPasswordNowhereButRequestBodies(fetchSpy, client);
  });
});

describe('statement details for a locked PDF', () => {
  const detail = (code: string): StatementDetailData => ({
    statement: {
      ...tangerine,
      id: 'locked-1',
      title: 'Received Sep 3, 2026',
      source: 'manualUpload',
      filename: 'tangerine-sep.pdf',
      status: 'failed',
      failureCode: code,
      failureMessage: 'This PDF is password-protected. Enter its password to unlock it.',
      format: 'pdf',
    },
    subject: null,
    currency: 'CAD',
    openingBalance: null,
    closingBalance: null,
    detectionReasons: [],
    warnings: [],
    transactions: [],
  });

  it('asks for the file again when it isn’t held any more, then unlocks it with the password', async () => {
    const fetchSpy = mockApi({
      '/api/jobs/active': () => new Response(null, { status: 204 }),
      '/api/statements/locked-1': detail('pdf_password_protected'),
      '/api/statements': [],
      'POST /api/uploads/statements': { jobId: 'j9', statementId: 'locked-1' },
      '/api/jobs/j9': job('j9'),
    });
    const { client } = renderWithApp(
      <JobsProvider enabled quiet>
        <StatementDetail statementId="locked-1" dateFormat="MMM d, yyyy" currency="CAD" onClose={() => undefined} />
      </JobsProvider>,
    );

    const field = await screen.findByLabelText('PDF password', {}, WAIT);
    expect(screen.getByText(PASSWORD_HINT)).toBeInTheDocument();
    expect(field).toBeDisabled();
    expect(screen.getByText('FinSight doesn’t keep your files. Choose this PDF again to unlock it.')).toBeInTheDocument();
    expect(screen.getByText('Format').nextSibling).toHaveTextContent('PDF');

    const chooser = document.querySelector('input[type="file"][accept=".pdf,application/pdf"]') as HTMLInputElement;
    expect(chooser).not.toBeNull();
    act(() => fireEvent.click(screen.getByRole('button', { name: 'Choose file' })));
    const file = new File(['%PDF-1.4 encrypted'], 'tangerine-sep.pdf', { type: 'application/pdf' });
    act(() => fireEvent.change(chooser, { target: { files: [file] } }));

    await waitFor(() => expect(screen.getByLabelText('PDF password')).toBeEnabled());
    act(() => fireEvent.change(screen.getByLabelText('PDF password'), { target: { value: PASSWORD } }));
    act(() => fireEvent.click(screen.getByRole('button', { name: 'Unlock' })));

    await waitFor(() => expect(uploadBodies(fetchSpy)).toHaveLength(1), WAIT);
    const body = uploadBodies(fetchSpy)[0];
    expect(body?.get('file')).toBe(file);
    expect(body?.get('statementId')).toBe('locked-1');
    expect(body?.get('password')).toBe(PASSWORD);
    await waitFor(() => expect(fetchSpy.mock.calls.some(([input]) => String(input) === '/api/jobs/j9')).toBe(true), WAIT);
    expectPasswordNowhereButRequestBodies(fetchSpy, client);
  });

  it('says so when the last password was wrong', async () => {
    mockApi({ '/api/jobs/active': () => new Response(null, { status: 204 }), '/api/statements/locked-1': detail('pdf_password_incorrect') });
    renderWithApp(
      <JobsProvider enabled quiet>
        <StatementDetail statementId="locked-1" dateFormat="MMM d, yyyy" currency="CAD" onClose={() => undefined} />
      </JobsProvider>,
    );

    expect(await screen.findByText(WRONG_PASSWORD, {}, WAIT)).toBeInTheDocument();
    expect(screen.getByLabelText('PDF password')).toHaveAttribute('aria-invalid', 'true');
  });
});
