import { describe, expect, it } from 'vitest';
import { z } from 'zod';
import analysis from '@/test/fixtures/analysis.json';
import categories from '@/test/fixtures/categories.json';
import recurring from '@/test/fixtures/recurring.json';
import session from '@/test/fixtures/session.json';
import settings from '@/test/fixtures/settings.json';
import statements from '@/test/fixtures/statements.json';
import summary from '@/test/fixtures/summary.json';
import transactions from '@/test/fixtures/transactions.json';
import {
  analysisResponseSchema,
  categoryGroupSchema,
  jobSchema,
  recurringResponseSchema,
  sessionSchema,
  settingsSchema,
  statementSchema,
  summaryResponseSchema,
  transactionPageSchema,
} from './schemas';

/*
  Contract tests. The fixtures are real responses captured from the API's demo account, so these fail if the
  server and the client's runtime schemas drift apart.
*/
describe('response schemas accept real API responses', () => {
  it.each([
    ['session', sessionSchema, session],
    ['settings', settingsSchema, settings],
    ['summary', summaryResponseSchema, summary],
    ['analysis', analysisResponseSchema, analysis],
    ['transactions page', transactionPageSchema, transactions],
    ['categories', z.array(categoryGroupSchema), categories],
    ['recurring', recurringResponseSchema, recurring],
    ['statements', z.array(statementSchema), statements],
  ])('%s', (_name, schema, fixture) => {
    const result = schema.safeParse(fixture);
    expect(result.success, result.success ? '' : JSON.stringify(result.error.issues.slice(0, 3))).toBe(true);
  });
});

describe('response schemas reject contract drift', () => {
  it('rejects dates that are not yyyy-MM-dd', () => {
    const [first, ...others] = transactions.items;
    const page = { ...transactions, items: [{ ...first, date: '31/08/2026' }, ...others] };
    expect(transactionPageSchema.safeParse(page).success).toBe(false);
  });

  it('rejects unknown enum values instead of rendering them', () => {
    expect(settingsSchema.safeParse({ ...settings, theme: 'sepia' }).success).toBe(false);
    expect(
      jobSchema.safeParse({ id: 'j', kind: 'sync', status: 'paused', steps: [], errorCode: null, errorMessage: null, createdAt: '2026-09-16T10:00:00Z', completedAt: null }).success,
    ).toBe(false);
  });

  it('requires nullable fields to be present rather than silently missing', () => {
    const rest: Record<string, unknown> = { ...summary.summary };
    delete rest.savingsRate;
    expect(summaryResponseSchema.safeParse({ ...summary, summary: rest }).success).toBe(false);
  });
});
