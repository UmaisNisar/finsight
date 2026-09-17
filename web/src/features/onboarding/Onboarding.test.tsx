import { QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AiKey, GmailConnection, Institution, Job, Session, Statement } from '@/api/schemas';
import { SessionGate } from '@/app/App';
import { ConfirmProvider } from '@/app/providers/ConfirmProvider';
import { ToastProvider } from '@/app/providers/ToastProvider';
import { alert, CIBC_HINT } from '@/test/alerts';
import settings from '@/test/fixtures/settings.json';
import { deferred, json, mockApi, testQueryClient } from '@/test/utils';
import { OTHER_BANK_HINT } from './AddStatementsStep';
import { deriveSteps, inferSource, NO_CHOICES, type OnboardingChoices } from './steps';

afterEach(() => {
  vi.restoreAllMocks();
  sessionStorage.clear();
});

const WAIT = { timeout: 4000 };

function session(user: Partial<NonNullable<Session['user']>> = {}, capabilities: Partial<Session['capabilities']> = {}): Session {
  return {
    authenticated: true,
    user: { id: 'u1', name: 'Sam Rivera', email: 'umais@example.com', isDemo: false, onboardingCompleted: false, ...user },
    capabilities: { googleSignIn: true, gmail: true, ai: true, demo: true, ...capabilities },
  };
}

const notConnected: GmailConnection = { connected: false, email: null, status: null, connectedAt: null, lastSyncedAt: null };
const connected = (lastSyncedAt: string | null = '2026-09-16T10:00:00Z'): GmailConnection => ({ connected: true, email: 'sam.rivera@gmail.com', status: 'active', connectedAt: '2026-09-16T09:00:00Z', lastSyncedAt });
const noKey: AiKey = { hasUserKey: false, hint: null, serverKeyAvailable: true, model: 'gemini-2.5-flash' };

function statement(id: string, overrides: Partial<Statement> = {}): Statement {
  return {
    id,
    title: 'Statement',
    institution: `Bank ${id}`,
    accountType: 'chequing',
    accountMask: null,
    documentKind: 'bankStatement',
    source: 'gmail',
    filename: `${id}.pdf`,
    senderName: null,
    receivedAt: '2026-09-03T09:00:00Z',
    periodStart: null,
    periodEnd: null,
    status: 'discovered',
    failureCode: null,
    failureMessage: null,
    detectionConfidence: 0.9,
    extractionConfidence: null,
    transactionCount: 0,
    canReprocess: false,
    reprocessNeedsUpload: false,
    subject: `Your ${id} eStatement`,
    detectionReasons: [`Sent by Bank ${id}`, 'Attachment name looks like a statement'],
    signInUrl: null,
    downloadHint: null,
    ...overrides,
  };
}

const discovered = [
  statement('a'),
  statement('b'),
  statement('paystub', { documentKind: 'incomeDocument', detectionConfidence: 0.8 }),
  statement('weak', { detectionConfidence: 0.4 }),
];

function job(kind: Job['kind'], status: Job['status'], overrides: Partial<Job> = {}): Job {
  return {
    id: `job-${kind}`,
    kind,
    status,
    steps: [{ key: 'search', label: kind === 'sync' ? 'Finding statements' : 'Reading statements', status: status === 'succeeded' ? 'done' : 'running', detail: null }],
    errorCode: null,
    errorMessage: null,
    createdAt: '2026-09-16T10:00:00Z',
    completedAt: status === 'running' ? null : '2026-09-16T10:01:00Z',
    ...overrides,
  };
}

const noActiveJob = () => new Response(null, { status: 204 });

/** Remembers an answer to the first question for the tab, as it would be after the Google consent round trip. */
const rememberChoices = (choices: Partial<OnboardingChoices>) => sessionStorage.setItem('finsight.onboarding.u1.choices', JSON.stringify({ ...NO_CHOICES, ...choices }));

const institutions: Institution[] = [
  { id: 'cibc', name: 'CIBC', signInUrl: 'https://www.cibconline.cibc.com/', downloadHint: CIBC_HINT },
  { id: 'hsbc-uk', name: 'HSBC UK', signInUrl: null, downloadHint: null },
];

const pdf = (name: string) => new File(['%PDF-1.7'], name, { type: 'application/pdf' });

function renderGate(route: string, handlers: Record<string, unknown>) {
  const fetchSpy = mockApi({ '/api/settings': settings, '/api/jobs/active': noActiveJob, ...handlers });
  const client = testQueryClient();
  const router = createMemoryRouter(
    [
      {
        element: <SessionGate />,
        children: [
          { index: true, element: <h1>Overview page</h1> },
          { path: '*', element: <h1>Other page</h1> },
        ],
      },
    ],
    { initialEntries: [route] },
  );
  render(
    <QueryClientProvider client={client}>
      <ToastProvider>
        <ConfirmProvider>
          <RouterProvider router={router} />
        </ConfirmProvider>
      </ToastProvider>
    </QueryClientProvider>,
  );
  const calls = (method: string, path: string) => fetchSpy.mock.calls.filter(([input, init]) => String(input).split('?')[0] === path && (init?.method ?? 'GET') === method);
  return { router, client, calls };
}

const stepCard = (title: string | RegExp) => screen.getByRole('heading', { level: 2, name: title }).closest('li') as HTMLElement;
/** Step titles in order, without their screen reader state. */
const stepTitles = () => within(screen.getByRole('list', { name: 'Setup steps' })).getAllByRole('heading', { level: 2 }).map((h) => h.firstChild?.textContent);
const answer = (name: RegExp) => {
  act(() => fireEvent.click(screen.getByRole('radio', { name })));
  act(() => fireEvent.click(within(stepCard(/How do you get your statements/)).getByRole('button', { name: 'Continue' })));
};
const currentStep = () => document.querySelector('li[aria-current="step"]') as HTMLElement | null;
const expectCurrent = (title: RegExp) => waitFor(() => expect(within(currentStep() as HTMLElement).getByRole('heading', { level: 2 })).toHaveAccessibleName(title), WAIT);

describe('Onboarding gate', () => {
  it('shows onboarding to a signed-in account that hasn’t finished it, whatever the URL', async () => {
    const { router } = renderGate('/transactions?gmail=denied', { '/api/auth/session': session(), '/api/gmail': notConnected, '/api/statements': [], '/api/ai/key': noKey });
    expect(await screen.findByRole('heading', { level: 1, name: 'Set up FinSight' }, WAIT)).toBeInTheDocument();
    expect(screen.queryByText('Other page')).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/onboarding');
  });

  it.each([
    ['a demo account', session({ isDemo: true })],
    ['an account that finished onboarding', session({ onboardingCompleted: true })],
  ])('never shows onboarding to %s', async (_label, value) => {
    const { router } = renderGate('/onboarding', { '/api/auth/session': value });
    expect(await screen.findByRole('heading', { name: 'Overview page' }, WAIT)).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/');
    expect(screen.queryByText('Set up FinSight')).not.toBeInTheDocument();
  });

  it('treats sessions from servers without the onboarding flag as already set up', async () => {
    const legacy = { ...session(), user: { id: 'u1', name: 'Umais', email: 'u@example.com', isDemo: false } };
    renderGate('/', { '/api/auth/session': legacy });
    expect(await screen.findByRole('heading', { name: 'Overview page' }, WAIT)).toBeInTheDocument();
  });
});

describe('Connect Gmail', () => {
  it('explains a denied consent inline, offers a retry and removes the parameter', async () => {
    rememberChoices({ source: 'email' });
    const { router } = renderGate('/onboarding?gmail=denied', { '/api/auth/session': session(), '/api/gmail': notConnected, '/api/statements': [], '/api/ai/key': noKey });
    const alert = await screen.findByRole('alert', {}, WAIT);
    expect(alert).toHaveTextContent('Gmail wasn’t connected');
    await expectCurrent(/Connect Gmail/);
    expect(screen.getByRole('link', { name: 'Try again' })).toHaveAttribute('href', '/api/gmail/connect?returnTo=%2Fonboarding');
    expect(screen.getByRole('button', { name: 'Upload PDFs instead' })).toBeInTheDocument();
    await waitFor(() => expect(router.state.location.search).toBe(''));
  });

  it('skips the question and the Gmail steps when Gmail isn’t available on the server', async () => {
    renderGate('/onboarding', { '/api/auth/session': session({}, { gmail: false }), '/api/statements': [], '/api/ai/key': noKey, '/api/institutions': institutions });
    await expectCurrent(/Add your statements/);
    expect(stepTitles()).toEqual(['Add your statements', 'Add your Gemini API key', 'Import and analyze', 'Finish']);
    expect(screen.queryByRole('radiogroup')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Choose PDF files' })).toBeInTheDocument();
  });

  it('starts scanning straight after connecting, shows live progress, then the results to confirm', async () => {
    const jobResponse = deferred<unknown>();
    let statements: Statement[] = [];
    const { calls } = renderGate('/onboarding?gmail=connected', {
      '/api/auth/session': session(),
      '/api/gmail': connected(null),
      '/api/statements': () => statements,
      '/api/ai/key': noKey,
      'POST /api/statements/sync': { jobId: 'job-sync' },
      '/api/jobs/': () => jobResponse.promise,
    });

    expect(await screen.findByText('Looking through your inbox…', {}, WAIT)).toBeInTheDocument();
    await expectCurrent(/Scan your inbox/);
    expect(calls('POST', '/api/statements/sync')).toHaveLength(1);
    expect(within(currentStep() as HTMLElement).getByText('Connecting to Gmail')).toBeInTheDocument();

    statements = discovered;
    await act(async () => jobResponse.resolve(job('sync', 'succeeded')));
    await expectCurrent(/Confirm your statements/);
    expect(stepCard(/Scan your inbox/)).toHaveTextContent('Found 4 statements');
    expect(stepCard(/How do you get your statements/)).toHaveTextContent('By email');
    expect(calls('POST', '/api/statements/sync')).toHaveLength(1);
  });
});

describe('Scan', () => {
  it('suggests what to check, a rescan and downloading instead when nothing is found', async () => {
    renderGate('/onboarding', { '/api/auth/session': session(), '/api/gmail': connected(), '/api/statements': [], '/api/ai/key': noKey });
    expect(await screen.findByText('No statements found', {}, WAIT)).toBeInTheDocument();
    expect(screen.getByText(/arrive in this Gmail account/)).toBeInTheDocument();
    for (const name of ['Scan again', 'Download from my bank instead', 'Continue without statements']) expect(screen.getByRole('button', { name })).toBeInTheDocument();

    act(() => fireEvent.click(screen.getByRole('button', { name: 'Continue without statements' })));
    await expectCurrent(/Confirm your statements/);
  });

  it('offers the download path in one tap when a “Not sure” scan finds nothing', async () => {
    rememberChoices({ source: 'unsure' });
    renderGate('/onboarding', { '/api/auth/session': session(), '/api/gmail': connected(), '/api/statements': [], '/api/ai/key': noKey, '/api/institutions': institutions });
    expect(await screen.findByText(/probably doesn’t email statements/, {}, WAIT)).toBeInTheDocument();
    act(() => fireEvent.click(screen.getByRole('button', { name: 'Download from my bank instead' })));
    await expectCurrent(/Add your statements/);
    expect(stepTitles()).toEqual(['How do you get your statements?', 'Add your statements', 'Add your Gemini API key', 'Import and analyze', 'Finish']);
    expect(stepCard(/How do you get your statements/)).toHaveTextContent('Downloaded from your bank’s website');
  });

  it('offers a retry when the scan job fails', async () => {
    renderGate('/onboarding', {
      '/api/auth/session': session(),
      '/api/gmail': connected(null),
      '/api/statements': [],
      '/api/ai/key': noKey,
      'POST /api/statements/sync': { jobId: 'job-sync' },
      '/api/jobs/': job('sync', 'failed', { errorMessage: 'Gmail access has expired.' }),
    });
    const scan = await screen.findByRole('button', { name: 'Scan my inbox' }, WAIT);
    act(() => fireEvent.click(scan));
    expect(await screen.findByText('Gmail access has expired.', {}, WAIT)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument();
  });
});

describe('Confirm your statements', () => {
  const setup = () => renderGate('/onboarding', { '/api/auth/session': session(), '/api/gmail': connected(), '/api/statements': discovered, '/api/ai/key': noKey });
  const checkbox = (name: string) => within(screen.getByRole('list', { name: 'Possible statements' })).getByRole('checkbox', { name: new RegExp(name) });

  it('preselects likely statements, leaving pay stubs and weak matches unchecked with a label', async () => {
    setup();
    await expectCurrent(/Confirm your statements/);
    expect(checkbox('Bank a')).toBeChecked();
    expect(checkbox('Bank b')).toBeChecked();
    expect(checkbox('Bank paystub')).not.toBeChecked();
    expect(checkbox('Bank weak')).not.toBeChecked();
    expect(screen.getByText('Possible pay stub')).toBeInTheDocument();
    expect(screen.getByText('Weak match')).toBeInTheDocument();
    expect(screen.getByText('2 of 4 selected')).toBeInTheDocument();
    expect(screen.getByText('Your a eStatement')).toBeInTheDocument();
    expect(screen.getAllByText(/Sent by Bank a/)[0]).toBeInTheDocument();
  });

  it('needs at least one statement to continue, or an explicit skip', async () => {
    setup();
    await expectCurrent(/Confirm your statements/);
    expect(screen.getByRole('button', { name: 'Continue with 2 statements' })).toBeEnabled();

    act(() => fireEvent.click(screen.getByRole('button', { name: 'Select all' })));
    expect(screen.getByText('4 of 4 selected')).toBeInTheDocument();

    act(() => fireEvent.click(screen.getByRole('button', { name: 'Select none' })));
    expect(screen.getByRole('button', { name: 'Continue' })).toBeDisabled();

    act(() => fireEvent.click(checkbox('Bank weak')));
    expect(screen.getByRole('button', { name: 'Continue with 1 statement' })).toBeEnabled();

    act(() => fireEvent.click(screen.getByRole('button', { name: 'Skip for now' })));
    await expectCurrent(/Gemini API key/);
    expect(stepCard(/Confirm your statements/)).toHaveTextContent('Skipped for now');
  });
});

describe('Gemini API key', () => {
  async function toKeyStep(handlers: Record<string, unknown>) {
    const result = renderGate('/onboarding', { '/api/auth/session': session(), '/api/gmail': connected(), '/api/statements': discovered, ...handlers });
    await expectCurrent(/Confirm your statements/);
    act(() => fireEvent.click(screen.getByRole('button', { name: 'Skip for now' })));
    await expectCurrent(/Gemini API key/);
    return result;
  }

  const submit = (key: string) => {
    fireEvent.change(screen.getByLabelText('Gemini API key'), { target: { value: `  ${key}  ` } });
    fireEvent.click(screen.getByRole('button', { name: 'Verify & save' }));
  };

  it('guides to AI Studio and saves a verified key, showing only its hint', async () => {
    let saved = noKey;
    const { calls } = await toKeyStep({
      '/api/ai/key': () => saved,
      'PUT /api/ai/key': ({ init }: { init?: RequestInit }) => {
        expect(JSON.parse(String(init?.body))).toEqual({ apiKey: 'AIza-secret-Ab3x' });
        saved = { ...noKey, hasUserKey: true, hint: '…Ab3x' };
        return saved;
      },
    });
    expect(screen.getByRole('link', { name: /Google AI Studio/ })).toHaveAttribute('href', 'https://aistudio.google.com/apikey');
    expect(screen.getByRole('link', { name: /Google AI Studio/ })).toHaveAttribute('target', '_blank');
    expect(screen.getByLabelText('Gemini API key')).toHaveAttribute('type', 'password');
    act(() => fireEvent.click(screen.getByRole('button', { name: 'Show key' })));
    expect(screen.getByLabelText('Gemini API key')).toHaveAttribute('type', 'text');

    act(() => submit('AIza-secret-Ab3x'));
    await expectCurrent(/Import and analyze|You’re all set/);
    expect(stepCard(/Gemini API key/)).toHaveTextContent('Key saved (…Ab3x)');
    expect(calls('PUT', '/api/ai/key')).toHaveLength(1);
  });

  it.each([
    [400, 'invalid_api_key', 'That key was rejected.', 'Google didn’t accept that key'],
    [429, 'rate_limited', 'Google is limiting requests for this key. Try again in a minute.', 'Google is limiting requests for this key'],
    [503, 'ai_unavailable', 'AI is unavailable.', 'couldn’t reach Google'],
    [403, 'demo_mode', 'Not in the demo.', 'The demo can’t save an API key'],
  ])('explains a %i %s error inline and keeps the step open', async (status, code, message, expected) => {
    await toKeyStep({ '/api/ai/key': noKey, 'PUT /api/ai/key': json({ code, message }, status) });
    act(() => submit('AIza-bad'));
    const alert = await screen.findByRole('alert', {}, WAIT);
    expect(alert).toHaveTextContent(expected);
    expect(screen.getByLabelText('Gemini API key')).toHaveAttribute('aria-invalid', 'true');
    await expectCurrent(/Gemini API key/);
  });

  it('is optional when FinSight has a built-in key', async () => {
    await toKeyStep({ '/api/ai/key': noKey });
    expect(screen.getByText(/FinSight’s built-in AI is used/)).toBeInTheDocument();
    act(() => fireEvent.click(screen.getByRole('button', { name: 'Skip' })));
    await expectCurrent(/Import and analyze|You’re all set/);
    expect(stepCard(/Gemini API key/)).toHaveTextContent('Using FinSight’s built-in AI');
  });

  it('still lets the user skip without a built-in key, saying AI stays off', async () => {
    await toKeyStep({ '/api/ai/key': { ...noKey, serverKeyAvailable: false } });
    expect(screen.getByText(/AI insights stay off/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Skip' })).not.toBeInTheDocument();
    act(() => fireEvent.click(within(stepCard(/Gemini API key/)).getByRole('button', { name: 'Skip for now' })));
    await waitFor(() => expect(stepCard(/Gemini API key/)).toHaveTextContent('AI features are off'), WAIT);
  });
});

describe('Import and finish', () => {
  it('imports the chosen statements with inline progress, then finishes into Overview', async () => {
    const processJob = deferred<unknown>();
    let statements = discovered;
    let completed = false;
    const { calls, router } = renderGate('/onboarding', {
      '/api/auth/session': () => session({ onboardingCompleted: completed }),
      '/api/gmail': connected(),
      '/api/statements': () => statements,
      '/api/ai/key': noKey,
      'POST /api/statements/process': { jobId: 'job-process' },
      '/api/jobs/': () => processJob.promise,
      'POST /api/onboarding/complete': () => {
        completed = true;
        return new Response(null, { status: 204 });
      },
    });

    await expectCurrent(/Confirm your statements/);
    act(() => fireEvent.click(screen.getByRole('button', { name: 'Continue with 2 statements' })));
    await expectCurrent(/Gemini API key/);
    act(() => fireEvent.click(screen.getByRole('button', { name: 'Skip' })));

    expect(await screen.findByText('Reading your statements…', {}, WAIT)).toBeInTheDocument();
    await expectCurrent(/Import and analyze/);
    const [processCall] = calls('POST', '/api/statements/process');
    expect(JSON.parse(String(processCall?.[1]?.body))).toEqual({ statementIds: ['a', 'b'] });

    statements = [
      statement('a', { status: 'processed', transactionCount: 40 }),
      statement('b', { status: 'failed', failureMessage: 'This PDF is password protected.' }),
      ...discovered.slice(2),
    ];
    await act(async () => processJob.resolve(job('process', 'succeeded')));

    await expectCurrent(/You’re all set/);
    expect(stepCard(/Import and analyze/)).toHaveTextContent('1 statement imported · 40 transactions');
    expect(screen.getByText('This PDF is password protected.')).toBeInTheDocument();
    expect(calls('POST', '/api/onboarding/complete')).toHaveLength(0);

    act(() => fireEvent.click(screen.getByRole('button', { name: 'Go to Overview' })));
    expect(await screen.findByRole('heading', { name: 'Overview page' }, WAIT)).toBeInTheDocument();
    expect(calls('POST', '/api/onboarding/complete')).toHaveLength(1);
    expect(router.state.location.pathname).toBe('/');
  });

  it('asks before setting up later when nothing has been imported', async () => {
    let completed = false;
    const { calls } = renderGate('/onboarding', {
      '/api/auth/session': () => session({ onboardingCompleted: completed }),
      '/api/gmail': notConnected,
      '/api/statements': [],
      '/api/ai/key': noKey,
      'POST /api/onboarding/complete': () => {
        completed = true;
        return new Response(null, { status: 204 });
      },
    });
    await expectCurrent(/How do you get your statements/);
    act(() => fireEvent.click(screen.getByRole('button', { name: 'Set up later' })));
    const dialog = await screen.findByRole('alertdialog', { hidden: true });
    expect(dialog).toHaveAccessibleName('Set up later?');
    act(() => fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel', hidden: true })));
    await act(async () => undefined);
    expect(calls('POST', '/api/onboarding/complete')).toHaveLength(0);

    act(() => fireEvent.click(screen.getByRole('button', { name: 'Set up later' })));
    act(() => fireEvent.click(within(screen.getByRole('alertdialog', { hidden: true })).getByRole('button', { name: 'Set up later', hidden: true })));
    expect(await screen.findByRole('heading', { name: 'Overview page' }, WAIT)).toBeInTheDocument();
    expect(calls('POST', '/api/onboarding/complete')).toHaveLength(1);
  });
});

describe('Resuming', () => {
  it('opens at Finish when statements are imported and a key is saved', async () => {
    renderGate('/onboarding', {
      '/api/auth/session': session(),
      '/api/gmail': connected(),
      '/api/statements': [statement('a', { status: 'processed', transactionCount: 12 })],
      '/api/ai/key': { ...noKey, hasUserKey: true, hint: '…Zz9q' },
    });
    await expectCurrent(/You’re all set/);
    expect(stepCard(/Gemini API key/)).toHaveTextContent('Key saved (…Zz9q)');
    expect(stepCard(/Connect Gmail/)).toHaveTextContent('Connected as sam.rivera@gmail.com');
  });

  it('resumes an import that is still running', async () => {
    renderGate('/onboarding', {
      '/api/auth/session': session(),
      '/api/gmail': connected(),
      '/api/statements': [statement('a', { status: 'processing' })],
      '/api/ai/key': noKey,
      '/api/jobs/active': job('process', 'running'),
      '/api/jobs/': job('process', 'running'),
    });
    await expectCurrent(/Import and analyze/);
    expect(screen.getByText('Reading your statements…')).toBeInTheDocument();
  });

  it('keeps interface choices for the tab, so a refresh stays on the same step', () => {
    const facts = { gmailAvailable: true, gmailConnected: true, statements: discovered, hasUserKey: false, scanning: false, importing: false, selectedCount: 2, replacingKey: false };
    expect(deriveSteps({ ...facts, choices: NO_CHOICES }).current).toBe('confirm');
    expect(deriveSteps({ ...facts, choices: { ...NO_CHOICES, confirmed: true } }).current).toBe('key');
    // A choice never marks a later step as done ahead of the user.
    expect(deriveSteps({ ...facts, choices: { ...NO_CHOICES, keySkipped: false, importSkipped: true } }).states.import).toBe('upcoming');
    expect(deriveSteps({ ...facts, gmailConnected: false, statements: [], choices: NO_CHOICES }).current).toBe('source');
    expect(deriveSteps({ ...facts, gmailConnected: false, statements: [], choices: { ...NO_CHOICES, source: 'email' } }).current).toBe('connect');
  });

  it('lists the steps of the highlighted answer before Continue, without completing the question', () => {
    const facts = { gmailAvailable: true, gmailConnected: false, statements: [] as Statement[], hasUserKey: false, scanning: false, importing: false, selectedCount: 0, replacingKey: false, choices: NO_CHOICES };
    const unsure = deriveSteps({ ...facts, previewSource: 'unsure' });
    expect(unsure.steps).toEqual(['source', 'connect', 'scan', 'confirm', 'key', 'import', 'finish']);
    expect(unsure.current).toBe('source');
    expect(unsure.path).toBe('undecided');
    expect(deriveSteps({ ...facts, previewSource: 'download' }).steps).toEqual(['source', 'add', 'key', 'import', 'finish']);
    // Reopening the question previews the new answer; progress keeps following the confirmed one.
    const changing = deriveSteps({ ...facts, choosingSource: true, previewSource: 'download', choices: { ...NO_CHOICES, source: 'email' } });
    expect(changing.steps).toContain('add');
    expect(changing.path).toBe('email');
  });

  it('infers the answer to the first question from what the server already knows', () => {
    const facts = { gmailAvailable: true, gmailConnected: false, statements: [] as Statement[], choices: NO_CHOICES };
    const upload = statement('u', { source: 'manualUpload', status: 'processed' });
    expect(inferSource(facts)).toBeNull();
    expect(inferSource({ ...facts, gmailConnected: true })).toBe('email');
    expect(inferSource({ ...facts, statements: [alert('a1', '2026-09-15T13:00:00Z')] })).toBe('email');
    expect(inferSource({ ...facts, statements: [upload] })).toBe('download');
    expect(inferSource({ ...facts, gmailConnected: true, statements: [upload] })).toBe('email');
    expect(inferSource({ ...facts, gmailAvailable: false, gmailConnected: true })).toBe('download');
    // An explicit answer wins.
    expect(inferSource({ ...facts, gmailConnected: true, choices: { ...NO_CHOICES, source: 'download' } })).toBe('download');
  });

  it('resumes on the download path when PDFs were uploaded and Gmail was never connected', async () => {
    renderGate('/onboarding', {
      '/api/auth/session': session(),
      '/api/gmail': notConnected,
      '/api/statements': [statement('u', { source: 'manualUpload', status: 'processed', title: 'August 2026', transactionCount: 30 })],
      '/api/ai/key': noKey,
      '/api/institutions': institutions,
    });
    await expectCurrent(/Add your statements/);
    expect(stepTitles()).toEqual(['How do you get your statements?', 'Add your statements', 'Add your Gemini API key', 'Import and analyze', 'Finish']);
    expect(within(screen.getByRole('list', { name: 'Added by you' })).getByText('August 2026')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Continue' })).toBeEnabled();
  });
});

describe('How do you get your statements?', () => {
  const fresh = () => renderGate('/onboarding', { '/api/auth/session': session(), '/api/gmail': notConnected, '/api/statements': [], '/api/ai/key': noKey, '/api/institutions': institutions });

  it('asks first, as radio rows that need a choice before continuing', async () => {
    fresh();
    await expectCurrent(/How do you get your statements/);
    const radios = screen.getAllByRole('radio');
    expect(radios.map((r) => r.textContent)).toEqual([
      'By emailMy bank emails statements, or tells me when one is ready.',
      'I download them from my bank’s websiteI sign in to online banking to get PDFs.',
      'Not sureFinSight can check your inbox for you.',
    ]);
    expect(radios.every((r) => r.getAttribute('aria-checked') === 'false')).toBe(true);
    expect(within(stepCard(/How do you get your statements/)).getByRole('button', { name: 'Continue' })).toBeDisabled();

    // Arrow keys move the selection, like any radio group.
    act(() => fireEvent.click(radios[0] as HTMLElement));
    act(() => fireEvent.keyDown(radios[0] as HTMLElement, { key: 'ArrowDown' }));
    expect(screen.getByRole('radio', { name: /I download them/ })).toHaveAttribute('aria-checked', 'true');
    expect(screen.getByRole('radio', { name: /I download them/ })).toHaveFocus();
    expect(screen.getByRole('radio', { name: /By email/ })).toHaveAttribute('tabindex', '-1');
  });

  it.each([
    [/By email/, ['How do you get your statements?', 'Connect Gmail', 'Scan your inbox', 'Confirm your statements', 'Add your Gemini API key', 'Import and analyze', 'Finish'], /Connect Gmail/, 'By email'],
    [/Not sure/, ['How do you get your statements?', 'Connect Gmail', 'Scan your inbox', 'Confirm your statements', 'Add your Gemini API key', 'Import and analyze', 'Finish'], /Connect Gmail/, 'Not sure, so FinSight will check your inbox'],
    [/I download them/, ['How do you get your statements?', 'Add your statements', 'Add your Gemini API key', 'Import and analyze', 'Finish'], /Add your statements/, 'Downloaded from your bank’s website'],
  ])('shows the steps for %s', async (choice, titles, next, summary) => {
    fresh();
    await expectCurrent(/How do you get your statements/);
    expect(stepTitles()).toEqual(['How do you get your statements?', 'Add your statements', 'Add your Gemini API key', 'Import and analyze', 'Finish']);
    answer(choice);
    await expectCurrent(next);
    await waitFor(() => expect(stepTitles()).toEqual(titles), WAIT);
    expect(stepCard(/How do you get your statements/)).toHaveTextContent(summary);
    expect(JSON.parse(sessionStorage.getItem('finsight.onboarding.u1.choices') ?? '{}')).toMatchObject({ source: expect.any(String) });
  });

  it('explains what “Not sure” does before continuing', async () => {
    fresh();
    await expectCurrent(/How do you get your statements/);
    act(() => fireEvent.click(screen.getByRole('radio', { name: /Not sure/ })));
    expect(screen.getByText(/switch to downloading PDFs in one tap/)).toBeInTheDocument();
    answer(/Not sure/);
    await expectCurrent(/Connect Gmail/);
    expect(stepCard(/Connect Gmail/)).toHaveTextContent('FinSight will check whether your bank emails statements');
  });

  it('can be changed after Gmail is connected without disconnecting it', async () => {
    const { calls } = renderGate('/onboarding', { '/api/auth/session': session(), '/api/gmail': connected(null), '/api/statements': [], '/api/ai/key': noKey, '/api/institutions': institutions });
    await expectCurrent(/Scan your inbox/);
    expect(stepCard(/How do you get your statements/)).toHaveTextContent('By email');

    act(() => fireEvent.click(within(stepCard(/How do you get your statements/)).getByRole('button', { name: 'Change' })));
    await expectCurrent(/How do you get your statements/);
    expect(screen.getByRole('radio', { name: /By email/ })).toHaveAttribute('aria-checked', 'true');
    act(() => fireEvent.click(screen.getByRole('button', { name: 'Cancel' })));
    await expectCurrent(/Scan your inbox/);

    act(() => fireEvent.click(within(stepCard(/How do you get your statements/)).getByRole('button', { name: 'Change' })));
    await expectCurrent(/How do you get your statements/);
    answer(/I download them/);
    await expectCurrent(/Add your statements/);
    await waitFor(() => expect(stepTitles()).not.toContain('Connect Gmail'), WAIT);
    expect(calls('DELETE', '/api/gmail')).toHaveLength(0);

    // And back again: Gmail is still connected, so the Gmail path picks up at the scan.
    act(() => fireEvent.click(within(stepCard(/How do you get your statements/)).getByRole('button', { name: 'Change' })));
    answer(/By email/);
    await expectCurrent(/Scan your inbox/);
    expect(stepCard(/Connect Gmail/)).toHaveTextContent('Connected as sam.rivera@gmail.com');
  });
});

describe('Add your statements (download path)', () => {
  async function setup(handlers: Record<string, unknown> = {}) {
    rememberChoices({ source: 'download' });
    const result = renderGate('/onboarding', { '/api/auth/session': session(), '/api/gmail': notConnected, '/api/statements': [], '/api/ai/key': noKey, '/api/institutions': institutions, ...handlers });
    await expectCurrent(/Add your statements/);
    return result;
  }
  const choose = async (bank: string) => {
    const button = await screen.findByRole('button', { name: /Your bank/ }, WAIT);
    act(() => fireEvent.click(button));
    act(() => fireEvent.click(screen.getByRole('option', { name: bank, hidden: true })));
  };

  it('shows the chosen bank’s instructions and a safe link to sign in, or general steps', async () => {
    await setup();
    expect(await screen.findByRole('button', { name: /Your bank/ }, WAIT)).toHaveTextContent('Choose your bank');
    expect(screen.queryByTestId('bank-guide')).not.toBeInTheDocument();

    await choose('CIBC');
    const guide = await screen.findByTestId('bank-guide');
    expect(guide).toHaveTextContent(CIBC_HINT);
    const link = within(guide).getByRole('link', { name: /Open CIBC/ });
    expect(link).toHaveAttribute('href', 'https://www.cibconline.cibc.com/');
    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');

    await choose('HSBC UK');
    await waitFor(() => expect(screen.getByTestId('bank-guide')).toHaveTextContent(OTHER_BANK_HINT));
    expect(within(screen.getByTestId('bank-guide')).queryByRole('link')).not.toBeInTheDocument();

    await choose('Other bank');
    await waitFor(() => expect(screen.getByTestId('bank-guide')).toHaveTextContent(OTHER_BANK_HINT));
    expect(screen.getByText('Tip: add the last 3–6 months for better insights.')).toBeInTheDocument();
  });

  it('continues once a PDF has started uploading, and keeps the list when switching bank', async () => {
    const { calls } = await setup({ 'POST /api/uploads/statements': { jobId: 'j1', statementId: 'u1' }, '/api/jobs/j1': job('upload', 'running', { id: 'j1' }) });
    const continueButton = () => within(stepCard(/Add your statements/)).getByRole('button', { name: 'Continue' });
    expect(continueButton()).toBeDisabled();

    await choose('CIBC');
    const input = stepCard(/Add your statements/).querySelector('input[type="file"]') as HTMLInputElement;
    expect(input).toHaveAttribute('multiple');
    act(() => fireEvent.change(input, { target: { files: [pdf('cibc-aug.pdf')] } }));

    const list = await screen.findByRole('list', { name: 'Added by you' }, WAIT);
    await waitFor(() => expect(within(list).getByText('cibc-aug.pdf')).toBeInTheDocument());
    await waitFor(() => expect(continueButton()).toBeEnabled());
    expect(calls('POST', '/api/uploads/statements')).toHaveLength(1);

    act(() => fireEvent.click(screen.getByRole('button', { name: 'Add statements from another bank' })));
    await waitFor(() => expect(screen.getByRole('button', { name: /Your bank/ })).toHaveTextContent('Choose your bank'));
    expect(within(screen.getByRole('list', { name: 'Added by you' })).getByText('cibc-aug.pdf')).toBeInTheDocument();

    act(() => fireEvent.click(continueButton()));
    await expectCurrent(/Gemini API key/);
    expect(stepCard(/Add your statements/)).toHaveTextContent('1 PDF added');
  });
});

describe('Confirm with statement alerts', () => {
  const cibcAlerts = [alert('jul', '2026-07-15T13:00:00Z'), alert('aug', '2026-08-15T13:00:00Z'), alert('sep', '2026-09-15T13:00:00Z')];

  it('shows only alert cards when the scan found nothing attached, and continues once an upload starts', async () => {
    const { calls } = renderGate('/onboarding', {
      '/api/auth/session': session(),
      '/api/gmail': connected(),
      '/api/statements': cibcAlerts,
      '/api/ai/key': noKey,
      'POST /api/uploads/statements': { jobId: 'j1', statementId: 'jul' },
      '/api/jobs/j1': job('upload', 'running', { id: 'j1' }),
    });
    await expectCurrent(/Confirm your statements/);
    expect(stepCard(/Scan your inbox/)).toHaveTextContent('Found 3 statement alerts');
    expect(screen.queryByRole('list', { name: 'Possible statements' })).not.toBeInTheDocument();
    expect(screen.queryByText(/of \d+ selected/)).not.toBeInTheDocument();

    const card = screen.getByRole('heading', { level: 3, name: 'CIBC credit card ending 5190' }).closest('section') as HTMLElement;
    expect(card).toHaveTextContent('3 statements ready · Jul, Aug, Sep');
    const continueButton = within(stepCard(/Confirm your statements/)).getByRole('button', { name: 'Continue' });
    expect(continueButton).toBeDisabled();

    act(() => fireEvent.change(card.querySelector('input[type="file"]') as HTMLInputElement, { target: { files: [pdf('july.pdf')] } }));
    await waitFor(() => expect(calls('POST', '/api/uploads/statements')).toHaveLength(1));
    await waitFor(() => expect(within(stepCard(/Confirm your statements/)).getByRole('button', { name: 'Continue' })).toBeEnabled());
    // A process request never includes alerts.
    expect(calls('POST', '/api/statements/process')).toHaveLength(0);
  });

  it('lists attachments to choose from and alerts to download side by side', async () => {
    renderGate('/onboarding', { '/api/auth/session': session(), '/api/gmail': connected(), '/api/statements': [...discovered, ...cibcAlerts], '/api/ai/key': noKey });
    await expectCurrent(/Confirm your statements/);
    expect(stepCard(/Scan your inbox/)).toHaveTextContent('Found 4 statements and 3 alerts');
    expect(within(screen.getByRole('list', { name: 'Possible statements' })).getAllByRole('checkbox')).toHaveLength(4);
    expect(screen.getByRole('heading', { level: 3, name: 'Statements to download' })).toBeVisible();
    expect(screen.getByText('These emails say a statement is ready but didn’t include the PDF.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Continue with 2 statements' })).toBeEnabled();
    act(() => fireEvent.click(screen.getByRole('button', { name: 'Select all' })));
    act(() => fireEvent.click(screen.getByRole('button', { name: 'Select none' })));
    expect(within(stepCard(/Confirm your statements/)).getByRole('button', { name: 'Continue' })).toBeDisabled();
  });

  it('treats a fulfilled alert as an upload: it is imported, not selected, and resume still lands in the right place', async () => {
    renderGate('/onboarding', {
      '/api/auth/session': session(),
      '/api/gmail': connected(),
      '/api/statements': [...discovered, { ...cibcAlerts[0], status: 'processing', title: 'July 2026' } as Statement, ...cibcAlerts.slice(1)],
      '/api/ai/key': noKey,
    });
    // Uploading one month doesn't count as importing the Gmail attachments, so the user stays on Confirm.
    await expectCurrent(/Confirm your statements/);
    expect(within(stepCard(/Confirm your statements/)).getByRole('list', { name: 'Added by you' })).toHaveTextContent('July 2026');
    expect(stepCard(/Confirm your statements/)).toHaveTextContent('2 statements ready · Aug, Sep');
  });
});
