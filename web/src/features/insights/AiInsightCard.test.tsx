import { act, fireEvent, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { keys } from '@/api/queries';
import { AiInsightCard, AnalysisCorrections } from '@/features/insights/AiComponents';
import base from '@/test/fixtures/analysis.json';
import session from '@/test/fixtures/session.json';
import { deferred, mockApi, renderWithApp, testQueryClient } from '@/test/utils';

afterEach(() => vi.restoreAllMocks());

const analysis = {
  summary: 'In August 2026, you earned $6,200 and spent $4,180, leaving $2,020.',
  keyInsights: [],
  savingsOpportunities: [],
  recurringExpenses: [],
  anomalies: [],
  recommendations: [],
  caveats: [],
};

type Overrides = { fallbackReason?: string | null; configured?: boolean; blocked?: string | null; source?: 'ai' | 'builtIn'; model?: string | null };

function response({ fallbackReason = null, configured = true, blocked = null, source = 'builtIn', model = null }: Overrides = {}) {
  return {
    ...base,
    state: 'fresh',
    analysis,
    generatedAt: '2026-09-16T12:00:00Z',
    model,
    source,
    fallbackReason,
    availability: { enabled: true, configured, blocked },
  };
}

function renderCard(body: unknown, { demo = false, generate }: { demo?: boolean; generate?: Promise<unknown> } = {}) {
  const fetchSpy = mockApi({ '/api/analysis': body, 'POST /api/analysis/generate': generate ?? body });
  const client = testQueryClient();
  client.setQueryData(keys.session, { ...session, user: { ...session.user, isDemo: demo } });
  renderWithApp(<AiInsightCard period={{ preset: 'last-month' }} hasData />, { client });
  return fetchSpy;
}

const card = () => screen.getByRole('region', { name: 'Financial summary' });

describe('a summary written without AI', () => {
  it.each([
    [{ fallbackReason: 'quota_exhausted' }, 'Written by FinSight without AI. Gemini’s daily limit was reached; try again after it resets.'],
    [{ fallbackReason: 'unavailable' }, 'Written by FinSight without AI. Gemini couldn’t be reached just now.'],
    [{ fallbackReason: 'limit_reached', blocked: 'limit_reached' }, 'Written by FinSight without AI. Today’s AI allowance is used up; it resets tomorrow.'],
    [{ fallbackReason: 'limit_reached', blocked: null }, 'Written by FinSight without AI.'],
    [{ fallbackReason: 'key_refused', blocked: 'key_refused' }, 'Written by FinSight without AI. Google refused the Gemini key. Update it in Settings.'],
    [{ fallbackReason: 'not_configured', configured: false }, 'Written by FinSight from your numbers, without AI.'],
  ] as const)('says why, plainly: %o', async (overrides, copy) => {
    renderCard(response(overrides));

    expect(await screen.findByText(analysis.summary)).toBeInTheDocument();
    expect(screen.getByText((_, el) => el?.tagName === 'P' && el.textContent === copy)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('links a refused key to Settings', async () => {
    renderCard(response({ fallbackReason: 'key_refused', blocked: 'key_refused' }));

    expect(await screen.findByRole('link', { name: 'Update it in Settings' })).toHaveAttribute('href', '/settings');
    expect(screen.queryByRole('button', { name: /with AI/ })).not.toBeInTheDocument();
  });

  it.each([
    [{ fallbackReason: 'quota_exhausted' }, 'Try again with AI'],
    [{ fallbackReason: 'unavailable' }, 'Try again with AI'],
    [{ fallbackReason: 'limit_reached', blocked: null }, 'Try again with AI'],
    [{ fallbackReason: 'not_configured', configured: true }, 'Analyze with AI'],
  ] as const)('offers AI again when a try could help: %o', async (overrides, label) => {
    renderCard(response(overrides));

    expect(await screen.findByRole('button', { name: label })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Add a Gemini key' })).not.toBeInTheDocument();
  });

  it.each([
    [{ fallbackReason: 'limit_reached', blocked: 'limit_reached' }],
    [{ fallbackReason: 'key_refused', blocked: 'key_refused' }],
  ] as const)('hides the AI button when a try can’t help: %o', async (overrides) => {
    renderCard(response(overrides));

    await screen.findByText(analysis.summary);
    expect(screen.queryByRole('button', { name: /with AI/ })).not.toBeInTheDocument();
  });

  it('offers to add a key instead when none is set up', async () => {
    renderCard(response({ fallbackReason: 'not_configured', configured: false }));

    expect(await screen.findByRole('link', { name: 'Add a Gemini key' })).toHaveAttribute('href', '/settings');
    expect(screen.queryByRole('button', { name: /with AI/ })).not.toBeInTheDocument();
  });

  it('doesn’t offer demo accounts a key they can’t add', async () => {
    renderCard(response({ fallbackReason: 'not_configured', configured: false }), { demo: true });

    await screen.findByText(analysis.summary);
    expect(screen.queryByRole('link', { name: 'Add a Gemini key' })).not.toBeInTheDocument();
  });

  it('shows nothing about AI for an AI summary', async () => {
    renderCard(response({ source: 'ai', model: 'gemini-3.5-flash' }));

    expect(await screen.findByRole('button', { name: 'Regenerate' })).toBeInTheDocument();
    expect(screen.queryByText(/without AI/)).not.toBeInTheDocument();
  });

  it('keeps the same card shell when AI replaces the built-in summary', async () => {
    const generated = deferred<unknown>();
    const fetchSpy = renderCard(response({ fallbackReason: 'quota_exhausted' }), { generate: generated.promise });

    const button = await screen.findByRole('button', { name: 'Try again with AI' });
    const shell = card();
    act(() => fireEvent.click(button));
    expect(await screen.findByText(/This takes about 20 seconds/)).toBeInTheDocument();
    expect(card()).toBe(shell);

    await act(async () => generated.resolve({ ...response({ source: 'ai', model: 'gemini-3.5-flash' }), analysis: { ...analysis, summary: 'Written by Gemini.' } }));

    expect(await within(shell).findByText('Written by Gemini.')).toBeInTheDocument();
    expect(card()).toBe(shell);
    expect(within(shell).queryByText(/without AI/)).not.toBeInTheDocument();
    expect(fetchSpy.mock.calls.filter(([input]) => String(input).startsWith('/api/analysis/generate'))).toHaveLength(1);
  });
});

describe('before any summary', () => {
  it('without a key, offers a summary FinSight writes and a way to add a key', async () => {
    renderCard({ ...base, availability: { enabled: true, configured: false } });

    expect(await screen.findByRole('button', { name: 'Summarize spending' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Add a Gemini key' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Analyze spending' })).not.toBeInTheDocument();
  });

  it('with a key, offers the AI analysis', async () => {
    renderCard(base);

    expect(await screen.findByRole('button', { name: 'Analyze spending' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Add a Gemini key' })).not.toBeInTheDocument();
  });
});

describe('corrections', () => {
  it('are not shown for a summary FinSight wrote itself', () => {
    renderWithApp(<AnalysisCorrections response={response({ fallbackReason: 'unavailable' }) as never} />);
    expect(screen.queryByText(/checked/)).not.toBeInTheDocument();
  });
});
