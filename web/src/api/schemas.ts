import { z } from 'zod';

/*
  Runtime contracts for every API response. Types are inferred from these schemas, so the client
  never trusts a shape it has not checked. They mirror server/FinSight.Api/Contracts/Dtos.cs.
*/

const isoDate = z.string().regex(/^\d{4}-\d{2}-\d{2}$/);
const isoDateTime = z.string();

export const capabilitiesSchema = z.object({
  googleSignIn: z.boolean(),
  gmail: z.boolean(),
  ai: z.boolean(),
  demo: z.boolean(),
});

export const sessionUserSchema = z.object({
  id: z.string(),
  name: z.string(),
  email: z.string(),
  isDemo: z.boolean(),
});

export const sessionSchema = z.object({
  authenticated: z.boolean(),
  user: sessionUserSchema.nullable(),
  capabilities: capabilitiesSchema,
});

export const themeSchema = z.enum(['system', 'light', 'dark']);

export const settingsSchema = z.object({
  currency: z.string(),
  dateFormat: z.string(),
  theme: themeSchema,
  aiCategorizationEnabled: z.boolean(),
  aiInsightsEnabled: z.boolean(),
  notificationsEnabled: z.boolean(),
});

export const gmailConnectionSchema = z.object({
  connected: z.boolean(),
  email: z.string().nullable(),
  status: z.enum(['active', 'expired']).nullable(),
  connectedAt: isoDateTime.nullable(),
  lastSyncedAt: isoDateTime.nullable(),
});

export const stepStatusSchema = z.enum(['pending', 'running', 'done', 'failed', 'skipped']);

export const jobSchema = z.object({
  id: z.string(),
  kind: z.enum(['sync', 'process', 'upload']),
  status: z.enum(['queued', 'running', 'succeeded', 'failed']),
  steps: z.array(z.object({ key: z.string(), label: z.string(), status: stepStatusSchema, detail: z.string().nullable() })),
  errorCode: z.string().nullable(),
  errorMessage: z.string().nullable(),
  createdAt: isoDateTime,
  completedAt: isoDateTime.nullable(),
});

export const jobStartedSchema = z.object({ jobId: z.string() });
export const uploadStartedSchema = z.object({ jobId: z.string(), statementId: z.string() });

export const accountTypeSchema = z.enum(['unknown', 'chequing', 'savings', 'creditCard', 'lineOfCredit', 'investment']);
export const statementStatusSchema = z.enum(['discovered', 'downloading', 'processing', 'processed', 'failed']);

export const statementSchema = z.object({
  id: z.string(),
  title: z.string(),
  institution: z.string().nullable(),
  accountType: accountTypeSchema,
  accountMask: z.string().nullable(),
  documentKind: z.enum(['unknown', 'bankStatement', 'creditCardStatement', 'incomeDocument']),
  source: z.enum(['gmail', 'manualUpload', 'demo']),
  filename: z.string(),
  senderName: z.string().nullable(),
  receivedAt: isoDateTime.nullable(),
  periodStart: isoDate.nullable(),
  periodEnd: isoDate.nullable(),
  status: statementStatusSchema,
  failureCode: z.string().nullable(),
  failureMessage: z.string().nullable(),
  detectionConfidence: z.number(),
  extractionConfidence: z.number().nullable(),
  transactionCount: z.number(),
  canReprocess: z.boolean(),
  reprocessNeedsUpload: z.boolean(),
});

export const transactionTypeSchema = z.enum(['income', 'expense', 'transfer']);

export const transactionSchema = z.object({
  id: z.string(),
  statementId: z.string(),
  date: isoDate,
  postingDate: isoDate.nullable(),
  description: z.string(),
  merchant: z.string(),
  amount: z.number(),
  currency: z.string(),
  type: transactionTypeSchema,
  categoryId: z.string(),
  categoryName: z.string(),
  groupId: z.string(),
  categorySource: z.enum(['default', 'rule', 'ai', 'user']),
  categoryConfidence: z.number(),
  isRefund: z.boolean(),
  isReversal: z.boolean(),
  isExcluded: z.boolean(),
  isEdited: z.boolean(),
  account: z.object({ institution: z.string().nullable(), accountType: accountTypeSchema, mask: z.string().nullable() }),
});

export const statementDetailSchema = z.object({
  statement: statementSchema,
  subject: z.string().nullable(),
  currency: z.string().nullable(),
  openingBalance: z.number().nullable(),
  closingBalance: z.number().nullable(),
  detectionReasons: z.array(z.string()),
  warnings: z.array(z.string()),
  transactions: z.array(transactionSchema),
});

export const transactionPageSchema = z.object({
  items: z.array(transactionSchema),
  total: z.number(),
  page: z.number(),
  pageSize: z.number(),
  moneyIn: z.number(),
  moneyOut: z.number(),
});

export const categoryGroupSchema = z.object({
  id: z.string(),
  name: z.string(),
  categories: z.array(
    z.object({ id: z.string(), name: z.string(), groupId: z.string(), kind: z.enum(['income', 'expense', 'transfer']), isCustom: z.boolean() }),
  ),
});

const dateRangeSchema = z.object({ start: isoDate, end: isoDate });

export const periodSchema = z.object({ preset: z.string(), start: isoDate, end: isoDate, label: z.string() });

export const categorySpendingSchema = z.object({
  categoryId: z.string(),
  name: z.string(),
  groupId: z.string(),
  groupName: z.string(),
  amount: z.number(),
  sharePercent: z.number(),
  transactionCount: z.number(),
  previousAmount: z.number(),
  changePercent: z.number().nullable(),
  monthlyAverage: z.number(),
  isFixed: z.boolean(),
});

export const summarySchema = z.object({
  range: dateRangeSchema,
  income: z.number(),
  expenses: z.number(),
  grossSpending: z.number(),
  refunds: z.number(),
  netCashFlow: z.number(),
  savingsRate: z.number().nullable(),
  averageMonthlyIncome: z.number(),
  averageMonthlyExpenses: z.number(),
  fixedExpenses: z.number(),
  variableExpenses: z.number(),
  fees: z.number(),
  interestEarned: z.number(),
  transfers: z.object({ count: z.number(), total: z.number() }),
  previous: z.object({
    range: dateRangeSchema,
    hasData: z.boolean(),
    income: z.number(),
    expenses: z.number(),
    incomeChangePercent: z.number().nullable(),
    expenseChangePercent: z.number().nullable(),
  }),
  categories: z.array(categorySpendingSchema),
  groups: z.array(
    z.object({
      groupId: z.string(),
      name: z.string(),
      amount: z.number(),
      sharePercent: z.number(),
      previousAmount: z.number(),
      changePercent: z.number().nullable(),
    }),
  ),
  incomeSources: z.array(z.object({ categoryId: z.string(), name: z.string(), amount: z.number(), transactionCount: z.number() })),
  topMerchants: z.array(
    z.object({ merchantKey: z.string(), merchant: z.string(), categoryId: z.string(), amount: z.number(), transactionCount: z.number() }),
  ),
  largestExpenses: z.array(z.object({ id: z.string(), date: isoDate, merchant: z.string(), categoryId: z.string(), amount: z.number() })),
  monthly: z.array(
    z.object({
      month: isoDate,
      income: z.number(),
      expenses: z.number(),
      netCashFlow: z.number(),
      savingsRate: z.number().nullable(),
      hasData: z.boolean(),
    }),
  ),
  transactionCount: z.number(),
  coverage: z.object({
    firstTransaction: isoDate.nullable(),
    lastTransaction: isoDate.nullable(),
    monthsInRange: z.number(),
    monthsWithData: z.number(),
    isPartial: z.boolean(),
  }),
});

export const recurringSchema = z.object({
  merchantKey: z.string(),
  merchant: z.string(),
  categoryId: z.string(),
  categoryName: z.string(),
  kind: z.enum(['subscription', 'bill', 'membership', 'loan', 'habit', 'income', 'other']),
  kindFromAi: z.boolean(),
  frequency: z.enum(['weekly', 'biweekly', 'monthly', 'quarterly', 'annual']),
  amount: z.number(),
  monthlyEquivalent: z.number(),
  amountVaries: z.boolean(),
  occurrences: z.number(),
  firstDate: isoDate,
  lastDate: isoDate,
  nextExpectedDate: isoDate,
  isActive: z.boolean(),
  confidence: z.number(),
  isIncome: z.boolean(),
});

export const anomalySchema = z.object({
  transactionId: z.string(),
  date: isoDate,
  merchant: z.string(),
  categoryId: z.string(),
  categoryName: z.string(),
  amount: z.number(),
  kind: z.enum(['unusuallyLarge', 'newMerchant', 'possibleDuplicate']),
  typicalAmount: z.number().nullable(),
});

export const summaryResponseSchema = z.object({
  period: periodSchema,
  currency: z.string(),
  summary: summarySchema,
  recurring: z.array(recurringSchema),
  anomalies: z.array(anomalySchema),
  latestTransactionDate: isoDate.nullable(),
  hasAnyData: z.boolean(),
});

export const recurringResponseSchema = z.object({
  items: z.array(recurringSchema),
  monthlyTotal: z.number(),
  monthlySubscriptions: z.number(),
  annualTotal: z.number(),
  aiReviewed: z.boolean(),
  currency: z.string(),
});

export const analysisSchema = z.object({
  summary: z.string(),
  keyInsights: z.array(z.object({ title: z.string(), description: z.string(), severity: z.enum(['info', 'attention', 'positive']) })),
  savingsOpportunities: z.array(
    z.object({
      categoryId: z.string(),
      category: z.string(),
      currentMonthlySpending: z.number(),
      suggestedMonthlyTarget: z.number().nullable(),
      estimatedMonthlySavings: z.number().nullable(),
      explanation: z.string(),
    }),
  ),
  recurringExpenses: z.array(z.object({ merchant: z.string(), amount: z.number(), frequency: z.string(), note: z.string().nullable() })),
  anomalies: z.array(
    z.object({ ref: z.string(), merchant: z.string(), description: z.string(), amount: z.number(), date: isoDate, explanation: z.string() }),
  ),
  recommendations: z.array(z.object({ title: z.string(), description: z.string(), potentialImpact: z.string().nullable() })),
  caveats: z.array(z.string()),
});

export const analysisResponseSchema = z.object({
  period: periodSchema,
  state: z.enum(['fresh', 'stale', 'none']),
  analysis: analysisSchema.nullable(),
  corrections: z.array(z.object({ section: z.string(), message: z.string() })),
  model: z.string().nullable(),
  generatedAt: isoDateTime.nullable(),
  availability: z.object({ enabled: z.boolean(), configured: z.boolean() }),
  currency: z.string(),
});

export const apiErrorSchema = z.object({ code: z.string(), message: z.string() });

export type Capabilities = z.infer<typeof capabilitiesSchema>;
export type Session = z.infer<typeof sessionSchema>;
export type Settings = z.infer<typeof settingsSchema>;
export type GmailConnection = z.infer<typeof gmailConnectionSchema>;
export type Job = z.infer<typeof jobSchema>;
export type JobStep = Job['steps'][number];
export type Statement = z.infer<typeof statementSchema>;
export type StatementDetail = z.infer<typeof statementDetailSchema>;
export type Transaction = z.infer<typeof transactionSchema>;
export type TransactionType = z.infer<typeof transactionTypeSchema>;
export type TransactionPage = z.infer<typeof transactionPageSchema>;
export type CategoryGroup = z.infer<typeof categoryGroupSchema>;
export type Summary = z.infer<typeof summarySchema>;
export type SummaryResponse = z.infer<typeof summaryResponseSchema>;
export type CategorySpending = z.infer<typeof categorySpendingSchema>;
export type Recurring = z.infer<typeof recurringSchema>;
export type RecurringResponse = z.infer<typeof recurringResponseSchema>;
export type Anomaly = z.infer<typeof anomalySchema>;
export type Analysis = z.infer<typeof analysisSchema>;
export type AnalysisResponse = z.infer<typeof analysisResponseSchema>;
