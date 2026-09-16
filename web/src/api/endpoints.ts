import { z } from 'zod';
import { request } from './client';
import {
  analysisResponseSchema,
  categoryGroupSchema,
  gmailConnectionSchema,
  jobSchema,
  jobStartedSchema,
  recurringResponseSchema,
  sessionSchema,
  sessionUserSchema,
  settingsSchema,
  statementDetailSchema,
  statementSchema,
  summaryResponseSchema,
  transactionPageSchema,
  transactionSchema,
  uploadStartedSchema,
  type Settings,
  type TransactionType,
} from './schemas';
import { periodQuery, type PeriodSelection } from '@/lib/period';

export interface TransactionFilters {
  search?: string;
  categoryId?: string;
  groupId?: string;
  type?: TransactionType;
  merchant?: string;
  statementId?: string;
  from?: string;
  to?: string;
  sort?: 'date-desc' | 'date-asc' | 'amount-desc' | 'amount-asc';
}

export interface TransactionUpdate {
  categoryId?: string;
  merchant?: string;
  type?: TransactionType;
  isExcluded?: boolean;
  applyToMerchant?: boolean;
  resetOverrides?: boolean;
}

function query(params: Record<string, string | number | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== '') {
      search.set(key, String(value));
    }
  }
  return search.toString();
}

export const api = {
  session: () => request('/api/auth/session', sessionSchema),
  startDemo: () => request('/api/auth/demo', sessionUserSchema, { method: 'POST' }),
  logout: () => request('/api/auth/logout', null, { method: 'POST' }),

  settings: () => request('/api/settings', settingsSchema),
  updateSettings: (settings: Settings) => request('/api/settings', settingsSchema, { method: 'PUT', body: settings }),

  gmail: () => request('/api/gmail', gmailConnectionSchema),
  disconnectGmail: () => request('/api/gmail', null, { method: 'DELETE' }),

  statements: () => request('/api/statements', z.array(statementSchema)),
  statement: (id: string) => request(`/api/statements/${id}`, statementDetailSchema),
  syncStatements: () => request('/api/statements/sync', jobStartedSchema, { method: 'POST' }),
  processStatements: (statementIds: string[]) =>
    request('/api/statements/process', jobStartedSchema, { method: 'POST', body: { statementIds } }),
  deleteStatement: (id: string) => request(`/api/statements/${id}`, null, { method: 'DELETE' }),
  uploadStatement: (file: File, statementId?: string) => {
    const form = new FormData();
    form.append('file', file);
    if (statementId) {
      form.append('statementId', statementId);
    }
    return request('/api/uploads/statements', uploadStartedSchema, { method: 'POST', body: form });
  },

  job: (id: string) => request(`/api/jobs/${id}`, jobSchema),
  activeJob: async () => {
    const response = await fetch('/api/jobs/active', { credentials: 'same-origin' });
    if (response.status !== 200) {
      return null;
    }
    const parsed = jobSchema.safeParse(await response.json());
    return parsed.success ? parsed.data : null;
  },

  transactions: (filters: TransactionFilters, page: number, pageSize = 50) =>
    request(`/api/transactions?${query({ ...filters, page, pageSize })}`, transactionPageSchema),
  updateTransaction: (id: string, update: TransactionUpdate) =>
    request(`/api/transactions/${id}`, transactionSchema, { method: 'PATCH', body: update }),

  categories: () => request('/api/categories', z.array(categoryGroupSchema)),

  summary: (period: PeriodSelection) => request(`/api/summary?${periodQuery(period)}`, summaryResponseSchema),
  analysis: (period: PeriodSelection) => request(`/api/analysis?${periodQuery(period)}`, analysisResponseSchema),
  generateAnalysis: (period: PeriodSelection) =>
    request(`/api/analysis/generate?${periodQuery(period)}`, analysisResponseSchema, { method: 'POST' }),
  recurring: () => request('/api/recurring', recurringResponseSchema),

  deleteTransactions: () => request('/api/data/transactions', z.object({ deleted: z.number() }), { method: 'DELETE' }),
  deleteStatements: () => request('/api/data/statements', z.object({ deleted: z.number() }), { method: 'DELETE' }),
  deleteAllData: () => request('/api/data', null, { method: 'DELETE' }),
  deleteAccount: () => request('/api/account', null, { method: 'DELETE' }),
};
