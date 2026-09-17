import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Job, Session, Statement } from '@/api/schemas';
import { JobsProvider } from '@/app/providers/JobsProvider';
import { alert } from '@/test/alerts';
import settings from '@/test/fixtures/settings.json';
import { deferred, mockApi, renderWithApp } from '@/test/utils';
import StatementsPage from './StatementsPage';

afterEach(() => vi.restoreAllMocks());

const WAIT = { timeout: 4000 };

const session: Session = {
  authenticated: true,
  user: { id: 'u1', name: 'Umais', email: 'umais@example.com', isDemo: false, onboardingCompleted: true },
  capabilities: { googleSignIn: true, gmail: true, ai: true, demo: true },
};

const base = (id: string, status: Statement['status'], overrides: Partial<Statement> = {}): Statement => ({
  ...alert(id, '2026-09-03T09:00:00Z'),
  title: `Statement ${id}`,
  institution: 'Northwind Bank',
  accountType: 'chequing',
  accountMask: '4821',
  filename: `${id}.pdf`,
  status,
  reprocessNeedsUpload: false,
  signInUrl: null,
  downloadHint: null,
  ...overrides,
});

const statements: Statement[] = [
  base('found', 'discovered'),
  alert('jul', '2026-07-15T13:00:00Z'),
  base('reading', 'processing'),
  alert('aug', '2026-08-15T13:00:00Z'),
  base('broken', 'failed', { failureMessage: 'This PDF is password protected.' }),
  base('done', 'processed', { transactionCount: 12 }),
];

function renderPage(handlers: Record<string, unknown> = {}) {
  const fetchSpy = mockApi({
    '/api/auth/session': session,
    '/api/settings': settings,
    '/api/gmail': { connected: true, email: 'sam.rivera@gmail.com', status: 'active', connectedAt: null, lastSyncedAt: null },
    '/api/jobs/active': () => new Response(null, { status: 204 }),
    '/api/statements': statements,
    ...handlers,
  });
  const view = renderWithApp(
    <JobsProvider enabled>
      <StatementsPage />
    </JobsProvider>,
    { route: '/statements' },
  );
  return { ...view, fetchSpy };
}

const section = (title: string) => screen.getByRole('heading', { level: 2, name: title }).closest('section') as HTMLElement;

describe('Statements page with statement alerts', () => {
  it('lists alerts first, after the Gmail card, and keeps them out of every other section', async () => {
    renderPage();
    expect(await screen.findByRole('heading', { level: 2, name: 'Waiting for you to download' })).toBeInTheDocument();
    const headings = screen.getAllByRole('heading', { level: 2 }).map((h) => h.textContent);
    expect(headings).toEqual(['Gmail', 'Waiting for you to download', 'Ready to analyze', 'In progress', 'Needs attention', 'Analyzed']);

    expect(section('Waiting for you to download')).toHaveTextContent('2 statements ready · Jul, Aug');
    expect(screen.getByRole('button', { name: 'Analyze 1 statement' })).toBeInTheDocument();
    for (const title of ['Ready to analyze', 'In progress', 'Needs attention', 'Analyzed']) {
      expect(section(title)).not.toHaveTextContent('CIBC');
      expect(within(section(title)).getAllByRole('listitem')).toHaveLength(1);
    }
  });

  it('has no alert section when nothing is waiting', async () => {
    renderPage({ '/api/statements': statements.filter((s) => s.status !== 'awaitingUpload') });
    expect(await screen.findByRole('heading', { level: 2, name: 'Ready to analyze' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Waiting for you to download' })).not.toBeInTheDocument();
  });

  it('uploads several PDFs from the header one at a time, letting the server match alerts', async () => {
    const first = deferred<unknown>();
    let n = 0;
    const job = (id: string): Job => ({ id, kind: 'upload', status: 'succeeded', steps: [], errorCode: null, errorMessage: null, createdAt: '2026-09-16T10:00:00Z', completedAt: null });
    const { container, fetchSpy } = renderPage({
      'POST /api/uploads/statements': () => (++n === 1 ? first.promise : { jobId: `j${n}`, statementId: `s${n}` }),
      '/api/jobs/': ({ url }: { url: URL }) => job(url.pathname.split('/').pop() ?? ''),
    });
    await screen.findByRole('heading', { level: 2, name: 'Waiting for you to download' });
    const uploads = () => fetchSpy.mock.calls.filter(([input, init]) => String(input) === '/api/uploads/statements' && init?.method === 'POST').map(([, init]) => init?.body as FormData);

    // The header's button and picker come first; the alert card has its own.
    expect(screen.getAllByRole('button', { name: 'Upload PDFs' })).toHaveLength(2);
    const input = container.querySelector('input[type="file"]') as HTMLInputElement;
    expect(input.closest('[data-testid="statement-alert"]')).toBeNull();
    expect(input).toHaveAttribute('multiple');
    act(() => fireEvent.change(input, { target: { files: [new File(['%PDF'], 'jul.pdf', { type: 'application/pdf' }), new File(['%PDF'], 'aug.pdf', { type: 'application/pdf' })] } }));

    const list = await screen.findByRole('list', { name: 'Uploaded PDFs' });
    await waitFor(() => expect(uploads()).toHaveLength(1));
    expect(within(list).getAllByRole('listitem').map((li) => li.dataset.state)).toEqual(['uploading', 'queued']);

    await act(async () => first.resolve({ jobId: 'j1', statementId: 's1' }));
    await waitFor(() => expect(uploads()).toHaveLength(2), WAIT);
    expect(uploads().map((body) => body.get('statementId'))).toEqual([null, null]);
    await waitFor(() => expect(within(list).getAllByRole('listitem').map((li) => li.dataset.state)).toEqual(['done', 'done']), WAIT);
  });
});
