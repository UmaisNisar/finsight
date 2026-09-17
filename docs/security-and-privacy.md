# FinSight security and privacy

This is the source of truth for what FinSight does with your data. Every statement below was checked against the code, and each one names the file that enforces it. If the code changes, this document must change with it.

It describes the software. Anyone can run their own copy of FinSight, and how well a copy is protected also depends on the person running it (see [Limitations](#limitations-and-honest-caveats)).

Paths are relative to the repository root. `Api` means `server/FinSight.Api`, `Infra` means `server/FinSight.Infrastructure`, and `Core` means `server/FinSight.Core`.

## Contents

- [What FinSight collects](#what-finsight-collects)
- [What is stored, and how](#what-is-stored-and-how)
- [What is never stored](#what-is-never-stored)
- [What is shared, and with whom](#what-is-shared-and-with-whom)
- [Gmail access](#gmail-access)
- [Deleting your data](#deleting-your-data)
- [Security measures](#security-measures)
- [Limitations and honest caveats](#limitations-and-honest-caveats)
- [Suggested UI statements](#suggested-ui-statements)
- [Technical appendix](#technical-appendix)

## What FinSight collects

| Source | What FinSight gets | Where |
| --- | --- | --- |
| **Google sign-in** | Your Google account ID, email address and name. Sign-in asks only for the `openid`, `email` and `profile` scopes. It doesn't ask for Gmail access. | `Api/Auth/AuthenticationSetup.cs`, `Api/Auth/GoogleAccountLinker.cs` |
| **Gmail** (only if you connect it) | For emails that match statement searches: the message and thread IDs, the headers, Gmail's short preview text (the "snippet"), and attachment names, types and sizes. It downloads the PDF attachment of an email it recognizes as a statement, but only when that statement is processed. It also gets the email address of the Google account that granted access, and a refresh token. | `Infra/Gmail/GmailApiClient.cs`, `Infra/Pipeline/StatementDiscoveryService.cs`, `Api/Auth/GoogleAccountLinker.cs` |
| **Statement PDFs you upload** | The file's bytes, its name and its size. | `Api/Controllers/StatementsController.cs` |
| **Your edits** | Category, merchant name, type and exclusion changes on transactions; merchant rules; custom categories; settings (currency, date format, theme, AI switches). | `Api/Controllers/TransactionsController.cs`, `Api/Controllers/AccountController.cs` |
| **Your Gemini API key** (optional) | The key, which FinSight checks with Google before saving it. | `Api/Controllers/AiKeyController.cs` |

FinSight has no analytics, advertising or tracking scripts. The web app loads no third-party scripts, stylesheets or fonts. Fonts are bundled with the app, and the Content Security Policy only allows scripts and connections to FinSight's own origin (`web/index.html`, `web/package.json`, `Api/Middleware/SecurityMiddleware.cs`).

## What is stored, and how

Everything is stored in one SQLite database file on the server (`ConnectionStrings:FinSight`, by default `server/FinSight.Api/.data/finsight.db`). A few values are encrypted inside the database. **Most are not.** Being honest about which is which matters:

### Encrypted at rest

These are encrypted with ASP.NET Core Data Protection (AES-256-CBC with HMAC-SHA256). Each kind uses a separate purpose key (`Infra/Security/FieldProtector.cs`):

- **Transaction descriptions:** the statement line text, after card and account numbers are masked (`Infra/Persistence/FinSightDbContext.cs`, `Transaction.Description`).
- **Gmail refresh tokens** (`GmailConnection.EncryptedRefreshToken`).
- **Your Gemini API key** (`User.EncryptedGeminiApiKey`). Only its last four characters are kept in plain form, so Settings can show which key is saved.

### Stored in plain form (not encrypted)

- **Your account:** Google account ID, email address, name, settings, and when you finished onboarding.
- **Transactions:** date, posting date, **amount**, running balance, currency, merchant name, category, type, your edits, and whether a transaction is a transfer or refund.
- **Statements:** bank name, account type, **last four digits** of the account number, statement period, **opening and closing balances**, currency, masked file name, size, and a SHA-256 hash of the PDF. For Gmail statements it also stores the masked email subject, the sender (for example `CIBC <mailbox@cibc.com>`), when the email arrived, and Gmail's message, thread and attachment part IDs.
- **Gmail connection:** the Google email address that granted access, the granted scopes, its status and when it last synced.
- **Merchant rules:** merchant names with the category you or the AI chose, and the AI's short reason.
- **AI insights:** the text Gemini wrote about a period, which includes merchant names and amounts, stored after validation.
- **Processing jobs:** progress labels such as "CIBC credit card ending 5190".
- **Sessions:** one row per sign-in with a random session ID, your user ID and the sign-in time (`Api/Auth/SessionService.cs`).

Amounts and dates stay unencrypted so the database can total and filter them. Encrypting them would mean decrypting every transaction in memory for every chart. That trade-off is listed under [limitations](#limitations-and-honest-caveats).

### Masking before storage

Before anything is stored, card numbers (13–19 digits), other long digit runs (7–12 digits), and US SSN or Canadian SIN patterns in descriptions, email subjects and file names are replaced with `••••` plus at most the last four digits (`Core/Text/SensitiveDataMasker.cs`, applied in `Core/Parsing/StatementParser.cs`, `Infra/Pipeline/StatementDiscoveryService.cs` and `Api/Controllers/StatementsController.cs`). Masking works by pattern, so a number written in an unusual format might not be caught.

### Where the encryption keys are

The Data Protection key ring is stored in `DataProtection:KeysPath`, by default `server/FinSight.Api/.data/keys`, **next to the database** (`Api/Program.cs`). The keys are themselves encrypted at rest:

- with the certificate in `DataProtection:CertificatePath` when one is configured, or
- with Windows DPAPI (tied to the Windows account running FinSight) when there is no certificate and FinSight runs on Windows.

On Linux or macOS without a certificate, **the key ring is stored unencrypted**, and ASP.NET Core logs a warning when it writes a key. Anyone who can read both the database and an unencrypted key ring can decrypt the encrypted fields.

### How long data is kept

- **Your data:** until you delete it or your account. There is no automatic expiry for Google accounts.
- **Demo accounts:** deleted with their synthetic data once they're older than `Demo:RetentionHours` (default 24). A cleanup job checks every hour (`Infra/DependencyInjection.cs`, `Infra/Demo/DemoDataService.cs`).
- **Sessions:** end when you sign out, or 30 days after you signed in, whichever comes first (`Api/Auth/SessionService.cs`). A row for a session that simply expired stays until your next sign-in clears it, or until your account is deleted.
- **In server memory only:**
  - Gmail access tokens (up to about an hour).
  - AI labels for recurring payments (up to 12 hours).
  - Whether a session and user still exist (up to 30 seconds).
  - Uploaded PDFs, while they wait to be processed.

  All of these are gone when the server restarts.

## What is never stored

- **Statement PDFs.** Uploads are read into memory and never written to disk, not even as a temporary file while the upload arrives. PDFs downloaded from Gmail are held in memory only while they're processed. Only a SHA-256 hash is kept, to spot the same file twice (`Api/Controllers/StatementsController.cs`, `Infra/Pipeline/Jobs.cs`, `Infra/Pipeline/StatementImportService.cs`).
- **Full card or account numbers.** At most the last four digits are kept (see masking above).
- **Email bodies.** Gmail requests are limited to headers, the snippet and attachment details, and FinSight never downloads the message body (`Infra/Gmail/GmailApiClient.cs`). It doesn't store headers other than the masked subject and the sender, or the snippet.
- **Emails that aren't statements.** They're looked at in memory to classify them and then dropped. Only statements and statement-ready notices are recorded.
- **PDF passwords.** FinSight doesn't ask for them. Password-protected PDFs are rejected with a request to upload an unlocked copy (`Infra/Pdf/PdfPigTextExtractor.cs`).
- **Google access tokens** on disk, or any Google token in the browser. The refresh token stays encrypted on the server (`Infra/Gmail/GoogleTokenService.cs`).
- **Your Gemini key** in any response, log or error message (`Api/Controllers/AiKeyController.cs`, `Infra/Gemini/GeminiKeys.cs`).
- **Financial details in logs.** Logs record exception types, internal IDs (user, job, statement), HTTP status codes and model names. They don't record exception messages from the pipeline, transaction data, email content, tokens or keys (`Api/Middleware/ApiErrors.cs`, `Infra/Pipeline/JobRunner.cs`, `Infra/Gemini/GeminiClient.cs`, `Api/Auth/AuthenticationSetup.cs`).

## What is shared, and with whom

FinSight never sells data. It has no advertising and no analytics. It sends data to exactly two outside parties, both run by Google:

### Google (sign-in and Gmail)

- **Sign-in:** the standard OAuth exchange.
- **Gmail, if connected:** search queries, message and attachment requests, and token refreshes (`Infra/Gmail/GmailApiClient.cs`, `Infra/Gmail/GoogleTokenService.cs`).
- **Disconnecting:** FinSight asks Google to revoke the grant.

### Google Gemini (AI features)

Gemini is called only when AI is available (your own key or a server key) and the matching setting is on (`Infra/Gemini/GeminiService.cs`, `Infra/Gemini/GeminiPrompts.cs`, `Core/Insights/FinancialFacts.cs`):

| Feature | Setting | Sent to Gemini |
| --- | --- | --- |
| **Categorizing merchants** the built-in rules can't place, up to 150 per import (`Infra/Pipeline/CategorizationService.cs`) | AI categorization | The merchant name, one **masked statement descriptor**, whether money went in or out, the typical amount, and how many times it appeared. |
| **Labelling recurring payments** (subscription, bill and so on) when you open Recurring (`Api/Controllers/InsightsController.cs`) | AI categorization | For each series: merchant name, category, typical amount, frequency and count. |
| **Insights for a period** (`Infra/Insights/AnalysisService.cs`) | AI insights | Totals (income, spending, savings rate, fixed and variable spending, fees, refunds, transfers), category totals and changes, income totals by category, top merchants, recurring expenses, the largest transactions and unusual transactions (date, merchant, category, amount), and a monthly trend. |

Gemini is **never** sent:

- your Google profile (name or email address)
- account numbers or their last four digits
- which bank or account a statement belongs to
- email content
- whole transaction lists
- the PDFs

The descriptor in categorization requests is the only raw statement text sent, and it's masked first.

Descriptors and merchant names come from your statements, so they can contain names. A payment to a person (for example an e-transfer) can have that person's name as its merchant name, and a transfer description can include your own name. Those can be sent to Gemini.

**Which key is used:** your own Gemini key if you saved one, otherwise the server's key (`Infra/Gemini/GeminiKeys.cs`). With your own key, requests go to Google under your Google account's Gemini API terms. With the server's key, they go under the terms of whoever runs the server. Google's Gemini API terms treat data differently on unpaid and paid tiers, so check the terms that apply to the key in use.

**Turning AI off:** switch off *AI categorization* and *AI insights* in Settings, and FinSight makes no Gemini calls with your data. Everything else keeps working with rules.

AI output is checked before it's stored or shown. Categories must exist, references must match what was sent, and figures are checked against the computed facts (`Core/Insights/AnalysisValidator.cs`, `Core/Insights/MerchantCategorization.cs`).

## Gmail access

- **Read-only:** FinSight asks for `https://www.googleapis.com/auth/gmail.readonly`. It can't send, delete, label or change email (`Infra/Gmail/GoogleIntegrationOptions.cs`, `Api/Controllers/GmailController.cs`).
- **Asked for separately:** Gmail access is requested only when you choose *Connect Gmail*, not at sign-in.
- **What is searched:** emails from the last 13 months (`Google:SearchMonths`), up to 250 per scan (`Google:MaxMessagesPerSync`). A message is only fetched if it matches one of these searches (`Core/Statements/StatementEmailClassifier.cs`):
  - PDF attachments with statement wording ("statement", "e-statement", "account summary")
  - statement subjects
  - pay stubs ("payslip", "pay stub", "earnings statement")
  - known bank senders mentioning a statement
  - subjects saying a statement "is ready" or "is available"
- **What is read:** for each matching message, its headers, Gmail's snippet and attachment details. For statements you process, the PDF attachment (up to 20 MB) is downloaded too.
- **Scans run when you ask:** a scan runs when you start one, and a PDF is downloaded when its statement is processed (`Api/Controllers/StatementsController.cs`). There is no background polling.
- **Another mailbox:** you can connect a different Google account's mailbox than the one you signed in with. It's attached to your FinSight account only.
- **Revoking:**
  - In FinSight, *Disconnect Gmail* in Settings, or *Delete all financial data*, revokes the grant at Google and deletes the stored token (`Infra/Gmail/GoogleTokenService.cs`). Statements already imported are kept unless you delete them.
  - You can also remove FinSight's access at any time at [myaccount.google.com/permissions](https://myaccount.google.com/permissions). FinSight marks the connection expired the next time it tries to use it.

## Deleting your data

All deletions are permanent and run as a single database transaction (`Api/Controllers/AccountController.cs`, `Api/Controllers/StatementsController.cs`):

| Action | Removes | Keeps |
| --- | --- | --- |
| **Delete a statement** | The statement and its transactions. Transfers matched to them are recategorized. | For a statement-ready notice from Gmail, an emptied, hidden row (message ID, masked subject, sender, bank, last four digits) so the next scan doesn't bring it back. |
| **Delete all transactions** | All transactions, uploaded statements and AI insights. | Gmail statement rows (reset to "ready to analyze"), merchant rules, custom categories, the Gmail connection, jobs, account and settings. |
| **Delete all statements** | All statements, all transactions and AI insights. | Merchant rules, custom categories, the Gmail connection, jobs, account and settings. |
| **Delete all financial data** | Revokes Gmail at Google, then deletes the Gmail connection, transactions, statements, merchant rules, custom categories, AI insights and processing jobs. | Your account (Google ID, email, name), settings, your saved Gemini key and your sessions. You stay signed in. |
| **Delete account** | Everything above, plus your sessions, your saved Gemini key and your account. Signs you out. Any copy of your session cookie stops working immediately. | Nothing in the database. |

Sessions on other devices are refused within 30 seconds, and immediately on the device you delete from (`Api/Middleware/SecurityMiddleware.cs`).

After deletion:

- The memory-only items listed under [How long data is kept](#how-long-data-is-kept) expire on their own schedule, or when the server restarts.
- A job that was already running when you deleted your data can't write to a deleted account.
- Deleted rows are removed from the database, but SQLite may leave their old bytes in free space or its write-ahead log file until they're overwritten. Backups made by the operator aren't touched. See [limitations](#limitations-and-honest-caveats).

## Security measures

- **Isolation between users:** every table of user data carries a database query filter bound to the signed-in user. Without a signed-in user, no rows match. Any attempt to save a row belonging to someone else is refused (`Infra/Persistence/FinSightDbContext.cs`, `Infra/Persistence/UserContext.cs`). Background jobs run as the user who started them (`Infra/Pipeline/JobWorker.cs`).
- **Sessions:**
  - The session cookie is HttpOnly and SameSite=Lax. In production it's `Secure` and uses the `__Host-` prefix (`Api/Auth/AuthenticationSetup.cs`).
  - The cookie is encrypted and signed, and expires after 7 days without use.
  - Each session is also recorded on the server. Signing out deletes it, so a copied cookie stops working, and every session ends 30 days after sign-in however active it is (`Api/Auth/SessionService.cs`).
- **Google OAuth:** state and correlation checks, PKCE, and redirect targets limited to FinSight's own pages (`Api/Auth/ReturnUrls.cs`). A Gmail grant is only attached to the account that started the connection (`Api/Auth/GoogleAccountLinker.cs`).
- **Cross-site request forgery:** every state-changing API request, uploads included, must carry the `X-FinSight-Request: 1` header, which other sites can't add (`Api/Middleware/SecurityMiddleware.cs`).
- **Upload and parsing limits:**
  - PDFs up to 20 MB, checked for a PDF header.
  - At most 30 uploads waiting per user, and 256 MB of waiting uploads across the server (`Uploads:MaxQueuedMegabytes`).
  - Other requests are limited to 1 MB (`Api/Program.cs`).
  - Parsing reads at most 80 pages, expands compressed content to at most 128 MB, and stops after 30 seconds (`Infra/Pdf/PdfPigTextExtractor.cs`).
- **Rate limits** (`Api/Controllers/RateLimits.cs`), per user when signed in and per IP address otherwise:
  - 600 requests a minute overall
  - 10 AI insight generations per 10 minutes
  - 10 Gemini key saves per 10 minutes
  - 12 Gmail scans or processing runs per 10 minutes
  - 120 uploads an hour
  - 20 demo sign-ins an hour per IP address
- **Headers** (`Api/Middleware/SecurityMiddleware.cs`):
  - Every response has `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy` (no camera, microphone, geolocation or payment) and `Cross-Origin-Opener-Policy: same-origin`.
  - The web app has a Content Security Policy: `default-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'`, with inline styles allowed.
  - API responses are `Cache-Control: no-store`.
- **Transport:** in production, HTTPS redirection and HSTS (`Api/Program.cs`). Forwarded headers are ignored unless the operator enables them for a reverse proxy.
- **Errors:** responses carry a stable code and a plain message, never stack traces or exception text (`Api/Middleware/ApiErrors.cs`). Accounts are keyed by Google account ID, so there's nothing to probe for account enumeration.
- **Development-only surfaces:** the OpenAPI document and the sign-in diagnostics endpoint exist only in Development (`Api/Program.cs`, `Api/Controllers/AuthController.cs`).
- **Repository:**
  - Dependabot updates NuGet, npm and GitHub Actions weekly (`.github/dependabot.yml`).
  - CodeQL scans C# and TypeScript (`.github/workflows/codeql.yml`).
  - CI runs with a read-only token and actions pinned to commit SHAs (`.github/workflows/ci.yml`).
  - Vulnerabilities can be reported privately ([SECURITY.md](../SECURITY.md)).

## Limitations and honest caveats

- **Most financial data isn't encrypted in the database.** Amounts, balances, dates, merchant names, bank names, last four digits, masked subjects, email addresses and AI insight text are stored in plain form. Anyone who can read the database file can read them. Only descriptions, Gmail tokens and Gemini keys are encrypted.
- **Encryption is only as strong as the key ring's protection.** The key ring sits next to the database by default. On Linux or macOS without `DataProtection:CertificatePath` it's unencrypted, and a copy of the `.data` folder then exposes the encrypted fields too. Operators should configure a certificate, keep keys out of database backups, and restrict file permissions.
- **The operator's deployment matters.** A self-hosted FinSight is only as secure as its server, its TLS and reverse proxy setup, its file permissions and backups, and who has access. With `ForwardedHeaders:Enabled` and no `KnownProxies`, the app trusts the last `X-Forwarded-For` hop. That's safe behind a proxy, but not if the app is also reachable directly. Operators should set `AllowedHosts`.
- **Deleted data can linger on disk.** SQLite doesn't overwrite deleted rows right away, and uses a write-ahead log. Old bytes can remain in free space or the log until they're reused. Operator backups aren't affected by in-app deletion. A shorter memory-only list is in [How long data is kept](#how-long-data-is-kept).
- **Data sent to Gemini** follows Google's Gemini API terms for the key in use, which differ between free and paid tiers. Merchant names and masked descriptors can contain people's names, including your own (for example e-transfers).
- **Masking is pattern-based.** Numbers in unusual formats (short reference numbers, numbers split by other characters) may not be masked.
- **Gmail access for unverified apps.** `gmail.readonly` is a restricted Google scope. Until the OAuth app passes Google's verification, it runs in Testing mode, with these consequences:
  - Only listed test users can connect.
  - Google shows an "unverified app" warning.
  - Refresh tokens expire after 7 days, so Gmail needs reconnecting.
- **Pay stubs are searched too.** If you connect Gmail, emails that look like pay stubs are found and can be imported like statements.
- **Demo mode** uses synthetic data only, but if the server has a Gemini key, demo users can generate AI insights with it, within the rate limits. Demo mode is off by default outside Development.
- **Parsing limits aren't absolute.** A PDF that hits a limit is abandoned, but PdfPig can't be interrupted partway through a single operation. A hostile file can still use CPU for the length of one page's work.
- **No end-to-end encryption.** The server has to read your data to analyze it.

## Suggested UI statements

Each of these is true of the code today. Keep the wording precise when adapting it.

1. Your statement PDFs are read in memory and never saved.
2. Card and account numbers are masked. FinSight keeps at most the last four digits.
3. Gmail access is read-only, and FinSight never downloads full email bodies.
4. You can disconnect Gmail at any time, and FinSight revokes its access at Google.
5. AI features never receive your PDFs, account numbers, or your Google name and email address.
6. Turn off AI categorization and AI insights in Settings, and none of your data is sent to Gemini.
7. Your Gemini API key is stored encrypted and is never shown again, apart from its last four characters.
8. Signing out ends your session on the server, not just in this browser.
9. Delete your account and everything in it is removed from FinSight's database right away.
10. FinSight has no ads, analytics or tracking, and never sells your data.

Avoid claims like "all your data is encrypted", "bank-level encryption", "nothing is sent to AI" or "deleted data is unrecoverable". They aren't true.

## Technical appendix

### Data flow

```text
Browser ──HTTPS, cookie + X-FinSight-Request──▶ ASP.NET Core API
  upload (≤20 MB, in memory) ─▶ JobQueue (memory, ≤256 MB total) ─▶ JobWorker (runs as the uploading user)
  Gmail: GoogleTokenService (refresh token decrypted in memory) ─▶ Gmail API (metadata, snippet, PDF attachment)
  PdfPigTextExtractor (80 pages, 128 MB decoded, 30 s) ─▶ StatementParser (mask) ─▶ SQLite (description encrypted)
  CategorizationService / AnalysisService ─▶ Gemini (merchant names, masked descriptors, aggregates)
```

### Cryptography

- **Data Protection:** ASP.NET Core defaults (AES-256-CBC with HMAC-SHA256, keys rotated every 90 days). Application name `FinSight`, and separate purposes `FinSight.Fields.v1`, `FinSight.OAuthTokens.v1` and `FinSight.GeminiApiKey.v1` (`Infra/Security/FieldProtector.cs`).
- **Encrypted descriptions** are stored as `enc:` plus the protected payload. A value that can't be decrypted (for example after the key ring is lost) reads as `[unavailable]`.
- **The key ring** is protected with `ProtectKeysWithCertificate` when `DataProtection:CertificatePath` is set (PKCS#12, password in `DataProtection:CertificatePassword`), otherwise `ProtectKeysWithDpapi` on Windows (`Api/Program.cs`).
- **PDF identity** is SHA-256 of the file bytes.

### Session model

- **Cookie:** cookie authentication. `finsight.session` in Development; `__Host-finsight`, `Secure` in other environments. HttpOnly, SameSite=Lax, 7-day sliding expiry.
- **Claims:** `finsight:uid` (internal user ID), `finsight:sid` (session ID), name, email and `finsight:demo`.
- **Server-side check:** `UserContextMiddleware` refuses a request whose user no longer exists, whose session row is gone, or whose session is older than 30 days, answering 401 `session_ended`. Positive checks are cached for 30 seconds. Sign-out and account deletion clear the cache entry for the current session.
- **Sessions without a session ID,** issued before this check existed, are refused, so their users sign in once more.

### Configuration that affects security

| Setting | Default | Effect |
| --- | --- | --- |
| `DataProtection:KeysPath` | `.data/keys` | Key ring location |
| `DataProtection:CertificatePath`, `DataProtection:CertificatePassword` | unset | Encrypts the key ring with a certificate |
| `ForwardedHeaders:Enabled`, `ForwardedHeaders:KnownProxies` | off | Trust `X-Forwarded-For` and `X-Forwarded-Proto` from a reverse proxy |
| `Demo:Enabled`, `Demo:RetentionHours` | off (on in Development), 24 | Demo sign-in and retention |
| `RateLimits:DemoPerHour`, `RateLimits:UploadsPerHour` | 20, 120 | Rate limits |
| `Uploads:MaxQueuedMegabytes` | 256 | Memory for waiting uploads |
| `Google:SearchMonths`, `Google:MaxMessagesPerSync` | 13, 250 | Gmail scan scope |
| `Gemini:ApiKey` | unset | Server-wide Gemini key, used when a user has none |

### Regression tests for these guarantees

- `server/FinSight.Tests/Api/SecurityApiTests.cs`: headers, CSP, CSRF, 401s, stable errors, sign-out, encrypted descriptions, return URLs, demo rate limit.
- `server/FinSight.Tests/Api/HardeningApiTests.cs`:
  - uploads never create temp files
  - the upload memory budget
  - cross-user IDs
  - server-side sign-out and the 30-day session lifetime
  - key ring encryption (certificate and DPAPI)
  - the 1 MB JSON limit on a real Kestrel server
- `server/FinSight.Tests/Infrastructure/PipelineTests.cs` (`PdfPigTextExtractorTests`): decompression bombs, the parse timeout and cancellation.
- `server/FinSight.Tests/Api/ApiTests.cs` and `DataApiTests.cs`: user isolation and deletion.
- `server/FinSight.Tests/Api/GoogleAuthApiTests.cs` and `OnboardingApiTests.cs`: the OAuth flow, Gmail linking and Gemini key handling.
