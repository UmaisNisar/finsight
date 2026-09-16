# FinSight

FinSight connects to Gmail, finds your bank and credit card statements, reads the PDFs, and explains where your money goes.

The pipeline is deliberately split so the AI never does arithmetic:

```
Gmail search ─▶ statement detection ─▶ PDF download (in memory) ─▶ layout-aware extraction
      ─▶ normalization ─▶ duplicate detection ─▶ rule categorization ─▶ AI categorization (only if needed)
      ─▶ transfer matching ─▶ deterministic metrics ─▶ Gemini explanation ─▶ validation ─▶ dashboard
```

- **Backend:** .NET 10 (ASP.NET Core, EF Core, PdfPig)
- **Frontend:** React 19 + TypeScript (Vite, TanStack Query, Recharts, Tailwind 4)
- **AI:** Gemini through one service, with structured output and validators

## Run it locally

Prerequisites: .NET SDK 10, Node 22+.

```bash
# Terminal 1: API on http://localhost:5085 (creates .data/finsight.db and applies migrations)
dotnet run --project server/FinSight.Api

# Terminal 2: web app on http://localhost:5173, proxying /api to the API
cd web
npm install
npm run dev
```

Open http://localhost:5173 and choose **Explore with sample data**. Demo mode is on in Development and needs no credentials: it creates a throwaway user with 12 months of synthetic transactions from a fictional bank. Demo users are deleted after 24 hours.

### Secrets

Secrets never go in `appsettings.json` or the frontend. In development, use user secrets:

```bash
cd server/FinSight.Api
dotnet user-secrets set "Gemini:ApiKey" "<key from https://aistudio.google.com/apikey>"
dotnet user-secrets set "Google:ClientId" "<oauth client id>"
dotnet user-secrets set "Google:ClientSecret" "<oauth client secret>"
```

In production, set environment variables instead: `Gemini__ApiKey`, `Google__ClientId`, `Google__ClientSecret`, `ConnectionStrings__FinSight`.

Without a Gemini key, everything works except AI categorization and insights. The UI says AI isn't configured, and every number is still calculated.

### Google OAuth and Gmail

1. In Google Cloud Console, create a project and **enable the Gmail API**.
2. Configure the OAuth consent screen. Add the scopes `openid`, `email`, `profile` and `https://www.googleapis.com/auth/gmail.readonly`. While the app is in Testing, add your Google account as a test user.
3. Create an OAuth client of type **Web application** with these redirect URIs:
   - `http://localhost:5173/api/auth/google/callback` (development, through the Vite proxy)
   - `https://<your-domain>/api/auth/google/callback` (production)
4. Store the client id and secret as shown above.

Sign-in only asks for basic profile scopes. Gmail access is requested separately (incremental consent) when the user clicks **Connect Gmail**, with offline access so later scans work.

> `gmail.readonly` is a Google *restricted* scope. Apps in Testing mode work for up to 100 test users. A public launch requires Google's OAuth verification and a security assessment.

## Checks

```bash
dotnet test FinSight.slnx          # 70 tests: parser, categorization, metrics, validators, API + ownership
cd web && npm run lint && npm run typecheck && npm test && npm run build
```

The API tests start the real app against a temporary SQLite database. They generate real PDFs and push them through upload, extraction, parsing and persistence. They also check that one user can never read or modify another user's data.

## Project layout

```
server/
  FinSight.Core/            Pure domain logic. No I/O, fully unit tested.
    Parsing/                Layout-aware statement parser (dates, money, columns, running balances)
    Normalization/          Fingerprints, merchant names, reversals, transfer matching
    Categories/             Category taxonomy, merchant catalog, rule engine
    Analytics/              Metrics, recurring detection, anomaly candidates, periods
    Insights/               Facts sent to Gemini, analysis schema, validators
    Statements/             Gmail statement detection, known institutions
  FinSight.Infrastructure/
    Persistence/            EF Core DbContext with per-user query filters, migrations
    Gmail/                  OAuth token refresh/revoke, Gmail REST client
    Pdf/                    PdfPig extractor (IPdfTextExtractor, where OCR plugs in)
    Gemini/                 GeminiClient, GeminiService, prompts, response schemas
    Pipeline/               Discovery, import, categorization, background jobs
    Insights/               Dashboard snapshot and analysis services
    Demo/                   Synthetic data generator
  FinSight.Api/             Controllers, auth, security middleware, rate limits
  FinSight.Tests/
web/src/
  api/                      zod schemas (runtime-validated contracts), client, endpoints, queries
  app/                      App, routing, shell, providers (jobs, toasts, theme)
  components/               UI primitives, period picker, progress checklist
  features/                 overview, transactions, statements, insights, recurring, settings, auth
  hooks/  lib/
```

## How the important parts work

**Statement detection.** Gmail is searched with several broad queries (statement, eStatement, account summary, pay stub; PDF attachments; last 13 months). Each email is scored on sender domain, subject, snippet and attachment names, with negative signals for receipts and invoices. Known institutions add confidence, but no bank is required. Candidates are stored as "discovered", and the user chooses what to analyze.

**Parsing.** PdfPig returns words with coordinates. The parser rebuilds lines, finds table headers (Date / Description / Withdrawals / Deposits / Amount / Balance, in many phrasings), and assigns each amount to a column by position. It handles:
- two-date card rows
- rows that omit a repeated date
- wrapped descriptions
- `CR`/`DR`, minus signs and parentheses
- day-first vs month-first dates
- years inferred across a December–January statement

Signs are resolved from column meaning first, then explicit markers, then the running balance. When the opening balance plus transactions equals the closing balance, the statement is marked reconciled and confidence is high.

**Duplicates and idempotency.**
- Statements are unique per user by source (`gmail:{messageId}:{partId}` or `upload:{sha256}`), and identical files are caught by content hash.
- Transactions carry a fingerprint (account, date, amount, description, occurrence), so overlapping statements never double-count.
- Reprocessing replaces a statement's transactions but keeps the user's edits.

**Transfers.** Card payments, own-account transfers and investment contributions are recognized by rules. Equal and opposite amounts across two of the user's accounts within four days are paired, when there is corroborating evidence. Transfers never count as income or spending. Refunds reduce spending instead of counting as income. Person-to-person payments count as spending but are flagged as uncertain.

**AI.** Gemini receives aggregates, category totals, merchant names and pre-selected anomaly candidates only: no account numbers, raw descriptions, email content or identity. Before anything is stored or shown, the response is validated:
- Categories, merchants and anomalies that aren't in the data are removed.
- Spending figures are overwritten with computed values, and savings are recalculated from targets.
- Currency figures in the prose that don't match any known number are flagged.

Every correction is visible in the UI.

## Security and privacy

- **Secrets:** OAuth refresh tokens are encrypted with ASP.NET Core Data Protection and never reach the browser. Access tokens exist only in server memory.
- **At rest:** transaction descriptions are encrypted. Card and account numbers are masked to the last four digits before storage, logging or AI.
- **PDFs:** processed in memory and never written to disk. Only their SHA-256 hash is kept.
- **Ownership:** every user-owned table has an EF global query filter bound to the signed-in user. An unset user matches nothing. `SaveChanges` also refuses writes to another user's rows.
- **Sessions:** HttpOnly, SameSite=Lax cookies (`__Host-` prefixed and Secure in production). State-changing requests require a custom header (CSRF defence).
- **Headers and limits:** security headers and CSP, `Cache-Control: no-store` on API responses, rate limits on AI, sync, upload and demo endpoints.
- **Errors and logs:** errors return a stable code and human copy, never stack traces. Logs record exception types and ids, not messages or financial data.
- **User control:** users can disconnect Gmail (the grant is revoked at Google) and delete transactions, statements, all data, or their account.

For production, also:
- serve over HTTPS
- protect the Data Protection key ring (for example `ProtectKeysWithCertificate` on Linux)
- consider database-level encryption

## Not built yet

These are designed for but not implemented. Nothing in the UI pretends otherwise.

- **OCR for scanned PDFs.** These are detected and reported as "appears to be a scanned image". An OCR engine would implement `IPdfTextExtractor`.
- **Password-protected PDFs.** These are detected and reported; there is no password prompt yet.
- **Notifications.** The preference is saved, but nothing is sent. The settings screen says so.
- **Currency conversion.** Amounts are shown in the selected currency without FX. Multi-currency accounts aren't converted.
- **Pay stubs** are discovered and labeled, but the parser targets bank and card statements.
- **Hosting at scale.** The job queue is in-process, which suits a single instance; a multi-instance deployment needs a durable queue. SQLite is the development database; the EF model is provider-agnostic for PostgreSQL.
- **Parser coverage.** It is bank-agnostic and tested on several layouts, but unusual formats will need real sample statements to tune.
