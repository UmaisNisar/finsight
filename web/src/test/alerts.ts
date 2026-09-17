import type { Statement } from '@/api/schemas';

/** Statement alert fixtures: "your statement is ready" emails with no PDF, as the API returns them. */
export const CIBC_HINT = 'Sign in to CIBC Online Banking, open My documents, and download each month’s statement as a PDF.';

export function alert(id: string, receivedAt: string, overrides: Partial<Statement> = {}): Statement {
  return {
    id,
    title: 'CIBC credit card ending 5190',
    institution: 'CIBC',
    accountType: 'creditCard',
    accountMask: '5190',
    documentKind: 'creditCardStatement',
    source: 'gmail',
    filename: '',
    senderName: 'CIBC',
    receivedAt,
    periodStart: null,
    periodEnd: null,
    status: 'awaitingUpload',
    failureCode: null,
    failureMessage: null,
    detectionConfidence: 0.9,
    extractionConfidence: null,
    transactionCount: 0,
    canReprocess: true,
    reprocessNeedsUpload: true,
    subject: 'Your CIBC eStatement is ready',
    detectionReasons: [],
    signInUrl: 'https://www.cibconline.cibc.com/ebm-resources/online-banking/client/index.html',
    downloadHint: CIBC_HINT,
    ...overrides,
  };
}

