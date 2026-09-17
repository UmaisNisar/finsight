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
- [Monthly summary email](#monthly-summary-email)
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
| **Statement files you upload** | The bytes, name and size of a PDF statement or a CSV, OFX or QFX transaction download. For a password-protected PDF, the password you type, used once to open it. | `Api/Controllers/StatementsController.cs` |
| **Your edits** | Category, merchant name, type and exclusion changes on transactions; merchant rules; custom categories; settings (currency, date format, theme, AI switches, automatic scan and import switches, monthly summary email). | `Api/Controllers/TransactionsController.cs`, `Api/Controllers/AccountController.cs` |
| **Your Gemini API key** (optional) | The key, which FinSight checks with Google before saving it. | `Api/Controllers/AiKeyController.cs` |

FinSight has no analytics, advertising or tracking scripts. The web app loads no third-party scripts, stylesheets or fonts. Fonts are bundled with the app, and the Content Security Policy only allows scripts and connections to FinSight's own origin (`web/index.html`, `web/package.json`, `Api/Middleware/SecurityMiddleware.cs`).

## What is stored, and how

Everything is stored in one database: a SQLite file on the server by default (`ConnectionStrings:FinSight`, by default `server/FinSight.Api/.data/finsight.db`), or a PostgreSQL database when `Database:Provider` is `Postgres`, as in the container deployment (`Infra/Persistence/DatabaseSetup.cs`). Both use the same model, so the same values are encrypted either way. A few values are encrypted inside the database. **Most are not.** Being honest about which is which matters:

### Encrypted at rest

These are encrypted with ASP.NET Core Data Protection (AES-256-CBC with HMAC-SHA256). Each kind uses a separate purpose key (`Infra/Security/FieldProtector.cs`):

- **Transaction descriptions:** the statement line text, after card and account numbers are masked (`Infra/Persistence/FinSightDbContext.cs`, `Transaction.Description`).
- **Gmail refresh tokens** (`GmailConnection.EncryptedRefreshToken`).
- **Your Gemini API key** (`User.EncryptedGeminiApiKey`). Only its last four characters are kept in plain form, so Settings can show which key is saved.

### Stored in plain form (not encrypted)

- **Your account:** Google account ID, email address, name, settings, and when you finished onboarding. For automation it also keeps when your next scheduled scan is due, when you turned on automatic import, the month of the last summary email sent, and a count of failed sends for the current month (`Core/Domain/Entities.cs`).
- **Transactions:** date, posting date, **amount**, running balance, currency, merchant name, category, type, your edits, and whether a transaction is a transfer or refund.
- **Statements:** bank name, account type, **last four digits** of the account number, statement period, **opening and closing balances**, currency, masked file name, size, the file's format (PDF, CSV, OFX or QFX), and a SHA-256 hash of the file. For Gmail statements it also stores the masked email subject, the sender (for example `CIBC <mailbox@cibc.com>`), when the email arrived, and Gmail's message, thread and attachment part IDs.
- **Gmail connection:** the Google email address that granted access, the granted scopes, its status and when it last synced.
- **Merchant rules:** merchant names with the category you or the AI chose, and the AI's short reason.
- **AI insights:** the text Gemini wrote about a period, which includes merchant names and amounts, stored after validation.
- **Processing jobs:** progress labels such as "CIBC credit card ending 5190".
- **Sessions:** one row per sign-in with a random session ID, your user ID and the sign-in time (`Api/Auth/SessionService.cs`).

Amounts and dates stay unencrypted so the database can total and filter them. Encrypting them would mean decrypting every transaction in memory for every chart. That trade-off is listed under [limitations](#limitations-and-honest-caveats).

### Masking before storage

Before anything is stored, card numbers (13–19 digits), other long digit runs (7–12 digits), and US SSN or Canadian SIN patterns in descriptions, email subjects and file names are replaced with `••••` plus at most the last four digits (`Core/Text/SensitiveDataMasker.cs`, applied in `Core/Parsing/StatementParser.cs`, `Core/Import/Csv/CsvStatementParser.cs`, `Core/Import/Ofx/OfxStatementParser.cs`, `Infra/Pipeline/StatementDiscoveryService.cs` and `Api/Controllers/StatementsController.cs`). Masking works by pattern, so a number written in an unusual format might not be caught.

CSV card-number and account-number columns, and OFX `ACCTID`, are reduced to their last four digits as the parser reads them; the full number is never stored. An OFX file's own transaction ID (`FITID`) is used only inside the fingerprint hash that spots duplicates, and isn't stored.

### Where the encryption keys are

The Data Protection key ring is stored in `DataProtection:KeysPath`, by default `server/FinSight.Api/.data/keys`, **next to the database** (`Api/Hosting/DataProtectionSetup.cs`). The container image uses `/data/keys` on a volume, separate from PostgreSQL's storage. The keys are themselves encrypted at rest:

- with the certificate in `DataProtection:CertificatePath` when one is configured, or
- with Windows DPAPI (tied to the Windows account running FinSight) when there is no certificate and FinSight runs on Windows.

On Linux or macOS without a certificate (which includes containers), FinSight **refuses to start** outside Development, so a deployment can't run with an unencrypted key ring by accident. The operator can override this with `DataProtection:AllowUnprotectedKeys=true`, meant for local testing such as the default `docker compose` setup. Then, and in Development, **the key ring is stored unencrypted**: FinSight logs a warning at startup (outside Development), and ASP.NET Core logs one when it writes a key. Anyone who can read both the database and an unencrypted key ring can decrypt the encrypted fields.

### How long data is kept

- **Your data:** until you delete it or your account. There is no automatic expiry for Google accounts.
- **Demo accounts:** deleted with their synthetic data once they're older than `Demo:RetentionHours` (default 24). A cleanup job checks every hour (`Infra/DependencyInjection.cs`, `Infra/Demo/DemoDataService.cs`).
- **Sessions:** end when you sign out, or 30 days after you signed in, whichever comes first (`Api/Auth/SessionService.cs`). A row for a session that simply expired stays until your next sign-in clears it, or until your account is deleted.
- **In server memory only:**
  - Gmail access tokens (up to about an hour).
  - AI labels for recurring payments (up to 12 hours).
  - AI usage counters for today's limits, and whether Google last refused your Gemini key or reported its quota used up (until midnight UTC, the next successful call, or a restart). No financial data is kept with them (`Infra/Gemini/AiUsage.cs`).
  - Merchants Gemini already looked at without placing them, and when AI categorization was last retried for you, so the same merchants aren't sent again (`Infra/Pipeline/PendingCategorizationRetry.cs`).
  - Whether a session and user still exist (up to 30 seconds).
  - Uploaded files, while they wait to be processed, and a PDF password sent with one. When the job ends, however it ends, the file's bytes are overwritten with zeros and the password reference is dropped (`Infra/Pipeline/Jobs.cs`, `Infra/Pipeline/JobWorker.cs`). .NET strings can't be wiped in place, so the password's characters stay in freed memory until the runtime reuses it.

  All of these are gone when the server restarts.

## What is never stored

- **Statement files.** Uploaded PDFs, CSVs, OFX and QFX files are read into memory and never written to disk, not even as a temporary file while the upload arrives. PDFs downloaded from Gmail are held in memory only while they're processed. Only a SHA-256 hash is kept, to spot the same file twice (`Api/Controllers/StatementsController.cs`, `Infra/Pipeline/Jobs.cs`, `Infra/Pipeline/StatementImportService.cs`).
- **Full card or account numbers.** At most the last four digits are kept (see masking above).
- **Email bodies.** Gmail requests are limited to headers, the snippet and attachment details, and FinSight never downloads the message body (`Infra/Gmail/GmailApiClient.cs`). It doesn't store headers other than the masked subject and the sender, or the snippet.
- **Emails that aren't statements.** They're looked at in memory to classify them and then dropped. Only statements and statement-ready notices are recorded.
- **PDF passwords.** A password you type to unlock a PDF goes in the body of that one upload request, is passed to the PDF reader for that one read, and is never stored in the database, written to job progress, logged or returned in a response or error. A wrong password fails with its own code, `pdf_password_incorrect`, whose message doesn't include it (`Api/Controllers/StatementsController.cs`, `Infra/Pipeline/StatementImportService.cs`, `Infra/Pdf/PdfPigTextExtractor.cs`). In the browser, the field is cleared after each attempt, and the password is never put in a URL, the query cache or browser storage. To retry, the browser keeps the chosen `File` in memory until it's read or removed from the list; after a reload, you choose the file again (`web/src/app/providers/JobsProvider.tsx`, `web/src/components/PdfPasswordPrompt.tsx`). A password sent with a file that isn't a PDF is dropped.
- **Google access tokens** on disk, or any Google token in the browser. The refresh token stays encrypted on the server (`Infra/Gmail/GoogleTokenService.cs`).
- **Your Gemini key** in any response, log or error message (`Api/Controllers/AiKeyController.cs`, `Infra/Gemini/GeminiKeys.cs`).
- **Financial details in logs.** Logs record exception types, internal IDs (user, job, statement), HTTP status codes and model names. They don't record exception messages from the pipeline, transaction data, email content, tokens or keys (`Api/Middleware/ApiErrors.cs`, `Infra/Pipeline/JobRunner.cs`, `Infra/Gemini/GeminiClient.cs`, `Api/Auth/AuthenticationSetup.cs`). Scheduled scans and summary emails log user IDs and exception types only: never an email address, a figure, an SMTP server reply or the SMTP password (`Infra/Automation/AutoScanService.cs`, `Infra/Email/DigestService.cs`, `Infra/Email/EmailSenders.cs`).
- **Summary emails.** A sent summary isn't stored. Only the month it covered is recorded, so it's never sent twice.

## What is shared, and with whom

FinSight never sells data. It has no advertising and no analytics. It sends data to Google (sign-in, Gmail and Gemini), and, only if you turn on monthly summary emails or send a test, to the email (SMTP) server the operator configured (see [Monthly summary email](#monthly-summary-email)):

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
- the statement files

The descriptor in categorization requests is the only raw statement text sent, and it's masked first.

Descriptors and merchant names come from your statements, so they can contain names. A payment to a person (for example an e-transfer) can have that person's name as its merchant name, and a transfer description can include your own name. Those can be sent to Gemini.

**Which key is used:** your own Gemini key if you saved one, otherwise the server's key (`Infra/Gemini/GeminiKeys.cs`). With your own key, requests go to Google under your Google account's Gemini API terms. With the server's key, they go under the terms of whoever runs the server. Google's Gemini API terms treat data differently on unpaid and paid tiers, so check the terms that apply to the key in use.

**Which models:** the configured model first (`Gemini:Model`), then the fallback models in `Gemini:FallbackModels` when it's out of quota or failing (`Infra/Gemini/GeminiClient.cs`). Every model receives exactly the same request, under the same key. Logs record only the model name, the HTTP status and a short reason; response bodies are never logged, because they can echo the request.

**Retrying categorization:** merchants that stayed uncategorized because AI wasn't available when you imported them are sent again later: at most once every 6 hours (`Ai:PendingRetryHours`) while you have a key, and shortly after you save one. It's the same data as the table above (merchant name, masked descriptor, direction, typical amount, count), and never for transactions you categorized yourself (`Infra/Pipeline/CategorizationService.cs`). Demo accounts and accounts with *AI categorization* off are skipped.

**Without AI:** when there's no key, Google refuses the key, every model's quota is used up, a daily limit is reached or Gemini fails, FinSight writes the insight summary itself from the same computed figures, and labels recurring payments from its own list of known merchants and categories (`Core/Insights/BuiltInAnalysis.cs`, `Core/Insights/RecurringLabeler.cs`). **Nothing is sent to Google on that path.** The summary is marked "Written by FinSight without AI" with the reason, and it's stored like an AI insight (the model column holds `finsight:built-in:<reason>`), so it's deleted in the same way.

**Daily limits:** FinSight counts Gemini calls per account per UTC day, by kind: categorization batches (`Ai:DailyLimits:Categorization`, default 40), insight summaries (`Ai:DailyLimits:Analysis`, 20) and recurring reviews (`Ai:DailyLimits:RecurringReview`, 20). Calls on the server's key also count towards a shared ceiling across all accounts (`Ai:GlobalDailyLimit`, 400); accounts listed in `Ai:PriorityEmails` are exempt from that ceiling, and calls on your own key never count towards it. Over a limit, nothing is sent and the built-in path is used. The counters live in memory, so they reset at midnight UTC and when the server restarts (`Infra/Gemini/AiUsage.cs`).

**Turning AI off:** switch off *AI categorization* and *AI insights* in Settings, and FinSight makes no Gemini calls with your data. Everything else keeps working with rules. (With *AI insights* off, FinSight doesn't write the built-in summary either.)

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
- **When scans run:** a scan runs when you start one (`Api/Controllers/StatementsController.cs`). If you turn on *Scan Gmail automatically* in Settings, FinSight also scans once a day at a time fixed for your account (`Automation:ScanInterval`), in the background, while you're signed out too (`Infra/Automation/AutoScanService.cs`). It's the same scan as the button, and it's off by default. Scheduled scans:
  - run only for accounts with an active Gmail connection, never for demo accounts
  - skip a day's scan if another job of yours is still running, and try again 30 minutes later
  - stop when Google rejects the grant: the connection is marked expired and isn't tried again until you reconnect
  - only find statements. Nothing is downloaded or imported unless you process it, with one opt-in exception below.
- **Automatic import (opt-in, off by default):** with *Import from banks you've used before* on, a statement found by a scheduled scan after you turned it on is downloaded and imported automatically, but only if its email shows the last four digits of the account (in the masked subject or file name) and you've already imported a statement from the same bank with the same last four digits. It never applies to statement-ready notices without a PDF, pay stubs, or statements whose email doesn't show the account's last four digits (`Infra/Automation/AutoScanService.cs`, `AutoImportRules`). Turning off automatic scans turns it off too.
- **PDF downloads:** a PDF is downloaded only when its statement is processed, by you or by automatic import.
- **Another mailbox:** you can connect a different Google account's mailbox than the one you signed in with. It's attached to your FinSight account only.
- **Revoking:**
  - In FinSight, *Disconnect Gmail* in Settings, or *Delete all financial data*, revokes the grant at Google and deletes the stored token (`Infra/Gmail/GoogleTokenService.cs`). Statements already imported are kept unless you delete them.
  - You can also remove FinSight's access at any time at [myaccount.google.com/permissions](https://myaccount.google.com/permissions). FinSight marks the connection expired the next time it tries to use it.

## Monthly summary email

Off by default. With *Monthly summary email* on in Settings, FinSight emails your sign-in address a summary of the previous month, from the 3rd of the next month, and only if that month has transactions (`Infra/Email/DigestService.cs`, `Infra/Email/MonthlyDigest.cs`). Demo accounts never get email. A server without email configured (`Email:*`, `App:PublicUrl`) sends nothing, and Settings says so.

- **What's in it:** the month's income, spending, net and savings rate; the top three spending categories with their change from the month before; recurring payments that started or stopped (merchant name and monthly amount); how many unusual transactions there were, with the largest one's merchant, amount and date; how many statement-ready notices wait for an upload and how many found statements aren't imported yet; and a link to open FinSight. If an AI insight for that month already exists and still matches your data, its one-paragraph summary is included. Every figure is calculated by FinSight, the same way as on the Overview. Writing a summary never calls Gemini.
- **What's never in it:** transaction descriptions, account numbers (not even the last four digits), bank names, email content from Gmail, or anything from the statement PDFs. It includes the line "Amounts only; open FinSight for details."
- **Merchant names can include people's names**, for example the recipient of an e-transfer, just as in the app.
- **No tracking:** no images, no tracking pixels, and links aren't rewritten or tracked (`Infra/Email/DigestRenderer.cs`).
- **Test emails:** *Send a test* in Settings sends you last month's summary now, marked as a test, even when summaries are off. Limited to 3 an hour.
- **Unsubscribing without signing in:** every summary has an unsubscribe link and `List-Unsubscribe` and `List-Unsubscribe-Post` headers (RFC 8058), so mail apps can offer one-click unsubscribe (`Api/Controllers/EmailController.cs`).
  - The link carries a token encrypted and signed with Data Protection under its own purpose (`FinSight.DigestUnsubscribe.v1`), naming your user ID and an expiry 60 days out (`Infra/Email/EmailLinks.cs`). A changed or expired token is refused.
  - Opening the link (GET) only shows a confirmation page and changes nothing, so link scanners can't unsubscribe you. A POST, from that page's button or from your mail app, turns summaries off and changes nothing else.
  - The endpoint reads no cookie or session. The token is the only credential, and it can only turn summaries off, so it's exempt from the `X-FinSight-Request` header check that mail apps can't send.
- **Sending once:** the month is recorded on your account before the message is handed to the mail server, so a restart or a second server instance never sends a month twice. If the server fails mid-send, that month's summary can be lost rather than duplicated. Failed deliveries are retried later (after 1, then 2 hours), at most 3 attempts per month.
- **Where it goes:** through the SMTP server the operator configured, over STARTTLS or TLS (`Email:Smtp:Security`; unencrypted only to a relay on the same machine). What happens after that depends on the mail providers involved, and the email stays in your mailbox until you delete it. In Development without an SMTP server, messages are written as `.eml` files to `.data/mail` on the server instead of being sent.

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

Summary emails already delivered stay in your mailbox; FinSight can't delete them. Deleting financial data disconnects Gmail, so scheduled scans stop even though the switch stays in your settings.

After deletion:

- The memory-only items listed under [How long data is kept](#how-long-data-is-kept) expire on their own schedule, or when the server restarts.
- A job that was already running when you deleted your data can't write to a deleted account.
- Deleted rows are removed from the database, but SQLite may leave their old bytes in free space or its write-ahead log file until they're overwritten, and PostgreSQL keeps them in its data files until vacuuming reuses the space, and in its write-ahead log. Backups made by the operator aren't touched. See [limitations](#limitations-and-honest-caveats).

## Security measures

- **Isolation between users:** every table of user data carries a database query filter bound to the signed-in user. Without a signed-in user, no rows match. Any attempt to save a row belonging to someone else is refused (`Infra/Persistence/FinSightDbContext.cs`, `Infra/Persistence/UserContext.cs`). Background jobs run as the user who started them (`Infra/Pipeline/JobWorker.cs`). The scheduler finds due users with filters switched off, then does each user's scan, import and summary in a separate unit of work bound to that user alone (`Infra/Automation/AutoScanService.cs`, `Infra/Email/DigestService.cs`).
- **Sessions:**
  - The session cookie is HttpOnly and SameSite=Lax. In production it's `Secure` and uses the `__Host-` prefix (`Api/Auth/AuthenticationSetup.cs`).
  - The cookie is encrypted and signed, and expires after 7 days without use.
  - Each session is also recorded on the server. Signing out deletes it, so a copied cookie stops working, and every session ends 30 days after sign-in however active it is (`Api/Auth/SessionService.cs`).
- **Google OAuth:** state and correlation checks, PKCE, and redirect targets limited to FinSight's own pages (`Api/Auth/ReturnUrls.cs`). A Gmail grant is only attached to the account that started the connection (`Api/Auth/GoogleAccountLinker.cs`).
- **Cross-site request forgery:** every state-changing API request, uploads included, must carry the `X-FinSight-Request: 1` header, which other sites can't add (`Api/Middleware/SecurityMiddleware.cs`).
- **Upload and parsing limits:**
  - Files up to 20 MB. The type is decided from the content, not the name: a `%PDF-` header, an OFX header or `<OFX>` root, or delimited text for CSV. Anything else is refused before it's queued (`Core/Import/StatementFileSniffer.cs`).
  - PDF passwords up to 256 characters.
  - At most 30 uploads waiting per user, and 256 MB of waiting uploads across the server (`Uploads:MaxQueuedMegabytes`).
  - Other requests are limited to 1 MB (`Api/Program.cs`).
  - PDF parsing reads at most 80 pages, expands compressed content to at most 128 MB, and stops after 30 seconds (`Infra/Pdf/PdfPigTextExtractor.cs`).
  - CSV and OFX parsing reads at most 20,000 transactions and stops after 15 seconds (`Core/Import/StatementFileParsing.cs`). OFX is read by a small tag scanner, not an XML parser, so nothing in the file (DTDs, entities, external references) is resolved, and nesting is capped (`Core/Import/Ofx/OfxStatementParser.cs`). A CSV whose columns can't be mapped with confidence fails with `csv_unrecognized` instead of being guessed.
- **Rate limits** (`Api/Controllers/RateLimits.cs`), per user when signed in and per IP address otherwise:
  - 600 requests a minute overall (the health endpoints are exempt)
  - 10 AI insight generations per 10 minutes
  - 10 Gemini key saves per 10 minutes
  - 12 Gmail scans or processing runs per 10 minutes (scheduled scans don't count against this, and happen at most once per `Automation:ScanInterval`)
  - 3 test summary emails an hour (`RateLimits:TestEmailsPerHour`)
  - 120 uploads an hour
  - 20 demo sign-ins an hour per IP address
- **Headers** (`Api/Middleware/SecurityMiddleware.cs`):
  - Every response has `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy` (no camera, microphone, geolocation or payment) and `Cross-Origin-Opener-Policy: same-origin`.
  - The web app has a Content Security Policy: `default-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'`, with inline styles allowed.
  - API responses are `Cache-Control: no-store`.
- **Transport:** in production, HTTPS redirection and HSTS (`Api/Program.cs`). Forwarded headers are ignored unless the operator enables them for a reverse proxy. The container listens on plain HTTP (port 8080) and expects a proxy to terminate TLS and redirect HTTP to HTTPS (README, Deployment).
- **Health endpoints:** `/health/live` and `/health/ready` are anonymous and exempt from rate limits. They answer only `Healthy` or `Unhealthy`, with no versions, host names or error details (`Api/Hosting/HealthEndpoints.cs`).
- **Errors:** responses carry a stable code and a plain message, never stack traces or exception text (`Api/Middleware/ApiErrors.cs`). Accounts are keyed by Google account ID, so there's nothing to probe for account enumeration.
- **Development-only surfaces:** the OpenAPI document and the sign-in diagnostics endpoint exist only in Development (`Api/Program.cs`, `Api/Controllers/AuthController.cs`).
- **Repository:**
  - Dependabot updates NuGet, npm and GitHub Actions weekly (`.github/dependabot.yml`).
  - CodeQL scans C# and TypeScript (`.github/workflows/codeql.yml`).
  - CI runs with a read-only token and actions pinned to commit SHAs (`.github/workflows/ci.yml`).
  - Container images are published only after CI passes, scanned with Trivy first (a critical vulnerability with a fix stops the release), and pushed with SBOM and provenance attestations. The release job's token can only read the repository, write packages and upload scan results (`.github/workflows/release.yml`).
  - The image runs as a non-root user on a chiseled base image with no shell or package manager (`Dockerfile`).
  - Vulnerabilities can be reported privately ([SECURITY.md](../SECURITY.md)).

## Limitations and honest caveats

- **Most financial data isn't encrypted in the database.** Amounts, balances, dates, merchant names, bank names, last four digits, masked subjects, email addresses and AI insight text are stored in plain form. Anyone who can read the database file can read them. Only descriptions, Gmail tokens and Gemini keys are encrypted.
- **Encryption is only as strong as the key ring's protection.** The key ring sits next to the database by default. On Linux or macOS without `DataProtection:CertificatePath` it's unencrypted in Development, or when an operator sets `DataProtection:AllowUnprotectedKeys`, and a copy of the `.data` folder (or the container's `/data` volume) then exposes the encrypted fields too. Operators should configure a certificate, keep keys and the certificate out of database backups, and restrict file permissions.
- **The operator's deployment matters.** A self-hosted FinSight is only as secure as its server, its TLS and reverse proxy setup, its file permissions and backups, and who has access. With `ForwardedHeaders:Enabled` and no `KnownProxies`, the app trusts the last `X-Forwarded-For` hop. That's safe behind a proxy, but not if the app is also reachable directly. Operators should set `AllowedHosts`.
- **Deleted data can linger on disk.** Neither SQLite nor PostgreSQL overwrites deleted rows right away, and both use a write-ahead log. Old bytes can remain in free space or the log until they're reused. Operator backups aren't affected by in-app deletion. A shorter memory-only list is in [How long data is kept](#how-long-data-is-kept).
- **Data sent to Gemini** follows Google's Gemini API terms for the key in use, which differ between free and paid tiers. Merchant names and masked descriptors can contain people's names, including your own (for example e-transfers).
- **Masking is pattern-based.** Numbers in unusual formats (short reference numbers, numbers split by other characters) may not be masked.
- **Email is less private than the app.** Summary emails contain your monthly totals and some merchant names. They pass through the operator's mail server and your email provider, and stay in your mailbox.
- **Automatic import reads the last four digits from the email.** A different number shown the same way in a bank's subject line (for example a masked reference number that happens to end in the same four digits) could be mistaken for the account. It's off by default and limited to banks and accounts you've already imported.
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

1. Your statement files (PDF, CSV, OFX, QFX) are read in memory and never saved. PDF passwords are used once and never stored.
2. Card and account numbers are masked. FinSight keeps at most the last four digits.
3. Gmail access is read-only, and FinSight never downloads full email bodies.
4. You can disconnect Gmail at any time, and FinSight revokes its access at Google.
5. AI features never receive your PDFs, account numbers, or your Google name and email address.
6. Turn off AI categorization and AI insights in Settings, and none of your data is sent to Gemini.
7. Your Gemini API key is stored encrypted and is never shown again, apart from its last four characters.
8. Signing out ends your session on the server, not just in this browser.
9. Delete your account and everything in it is removed from FinSight's database right away.
10. FinSight has no ads, analytics or tracking, and never sells your data.
11. Automatic Gmail scans only find statements. Nothing is imported without you, unless you choose to import automatically from accounts you've already imported. (Settings shows "Nothing is imported without you." under the scan switch, and the import switch appears right below it once scans are on.)
12. Summary emails contain amounts only, never account numbers or transaction descriptions.

Avoid claims like "all your data is encrypted", "bank-level encryption", "nothing is sent to AI" or "deleted data is unrecoverable". They aren't true.

## Technical appendix

### Data flow

```text
Browser ──HTTPS, cookie + X-FinSight-Request──▶ ASP.NET Core API
  upload (≤20 MB, in memory, type sniffed) ─▶ JobQueue (memory, ≤256 MB total) ─▶ JobWorker (runs as the uploading user; bytes and password cleared after)
  Gmail: GoogleTokenService (refresh token decrypted in memory) ─▶ Gmail API (metadata, snippet, PDF attachment)
  PdfPigTextExtractor (80 pages, 128 MB decoded, 30 s, optional password) ─▶ StatementParser (mask) ─┐
  CsvStatementParser / OfxStatementParser (20,000 rows, 15 s, mask) ──────────────────────────────┴▶ SQLite or PostgreSQL (description encrypted)
  CategorizationService / AnalysisService ─▶ Gemini (merchant names, masked descriptors, aggregates)
```

### Cryptography

- **Data Protection:** ASP.NET Core defaults (AES-256-CBC with HMAC-SHA256, keys rotated every 90 days). Application name `FinSight`, and separate purposes `FinSight.Fields.v1`, `FinSight.OAuthTokens.v1` and `FinSight.GeminiApiKey.v1` (`Infra/Security/FieldProtector.cs`), plus `FinSight.DigestUnsubscribe.v1` for unsubscribe tokens, which aren't stored (`Infra/Email/EmailLinks.cs`).
- **Encrypted descriptions** are stored as `enc:` plus the protected payload. A value that can't be decrypted (for example after the key ring is lost) reads as `[unavailable]`.
- **The key ring** is protected with `ProtectKeysWithCertificate` when `DataProtection:CertificatePath` is set (PKCS#12, password in `DataProtection:CertificatePassword`), otherwise `ProtectKeysWithDpapi` on Windows. Anywhere else, startup fails outside Development unless `DataProtection:AllowUnprotectedKeys` is set (`Api/Hosting/DataProtectionSetup.cs`).
- **File identity** is SHA-256 of the file bytes.

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
| `DataProtection:AllowUnprotectedKeys` | off | Lets FinSight start outside Development with an unencrypted key ring on Linux or macOS |
| `Database:Provider`, `ConnectionStrings:FinSight` | `Sqlite`, `.data/finsight.db` | Where data is stored (`Postgres` for containers) |
| `AllowedHosts` | `*` | Host names the app answers to. Set it in production |
| `ForwardedHeaders:Enabled`, `ForwardedHeaders:KnownProxies` | off | Trust `X-Forwarded-For` and `X-Forwarded-Proto` from a reverse proxy |
| `Demo:Enabled`, `Demo:RetentionHours` | off (on in Development), 24 | Demo sign-in and retention |
| `RateLimits:DemoPerHour`, `RateLimits:UploadsPerHour` | 20, 120 | Rate limits |
| `Uploads:MaxQueuedMegabytes` | 256 | Memory for waiting uploads |
| `Google:SearchMonths`, `Google:MaxMessagesPerSync` | 13, 250 | Gmail scan scope |
| `Gemini:ApiKey` | unset | Server-wide Gemini key, used when a user has none |
| `Gemini:Model`, `Gemini:FallbackModels` | `gemini-2.5-flash`, then `gemini-3.6-flash`, `gemini-3.5-flash`, `gemini-2.5-flash-lite`, `gemini-3.5-flash-lite`, `gemini-3.1-flash-lite` | Models tried in order; an empty `FallbackModels` turns fallback off |
| `Ai:DailyLimits:Categorization`, `Ai:DailyLimits:Analysis`, `Ai:DailyLimits:RecurringReview` | 40, 20, 20 | Gemini calls per account per UTC day, in memory |
| `Ai:GlobalDailyLimit`, `Ai:PriorityEmails` | 400, none | Calls per day on the server's key across all accounts, and accounts exempt from it |
| `Ai:PendingRetryEnabled`, `Ai:PendingRetryHours` | on, 6 | Background retry of AI categorization for merchants imported while AI was unavailable |
| `Automation:Enabled`, `Automation:ScanInterval` | on, 1 day | Scheduled Gmail scans and summary emails for users who opted in |
| `App:PublicUrl` | unset (`http://localhost:5173` in Development) | Address used in email links; must be HTTPS unless it's a local address. Without it, email is off |
| `Email:From`, `Email:Smtp:Host`, `Email:Smtp:Port`, `Email:Smtp:Username`, `Email:Smtp:Password`, `Email:Smtp:Security` | unset, 587, `StartTls` | Email delivery. The password belongs in user secrets or environment variables only |
| `RateLimits:TestEmailsPerHour` | 3 | Test summary emails per user |

### Regression tests for these guarantees

- `server/FinSight.Tests/Api/SecurityApiTests.cs`: headers, CSP, CSRF, 401s, stable errors, sign-out, encrypted descriptions, return URLs, demo rate limit.
- `server/FinSight.Tests/Api/HardeningApiTests.cs`:
  - uploads never create temp files
  - the upload memory budget
  - cross-user IDs
  - server-side sign-out and the 30-day session lifetime
  - key ring encryption (certificate and DPAPI)
  - the 1 MB JSON limit on a real Kestrel server
- `server/FinSight.Tests/Api/DeploymentApiTests.cs`: health endpoints reveal nothing and aren't rate limited, readiness fails without a database, and startup refuses an unencrypted key ring outside Development.
- `server/FinSight.Tests/Infrastructure/PostgresPersistenceTests.cs` and `Api/PostgresApiTests.cs` (run in CI against PostgreSQL): ownership filters, refused cross-user writes, bulk deletes, encryption at rest and exact money on PostgreSQL.
- `server/FinSight.Tests/Infrastructure/PipelineTests.cs` (`PdfPigTextExtractorTests`): decompression bombs, the parse timeout and cancellation.
- `server/FinSight.Tests/Api/PdfPasswordApiTests.cs`: PDF passwords never reach the database, job progress, responses or logs, and uploads are cleared when their job ends.
- `server/FinSight.Tests/Api/StructuredImportApiTests.cs` and `server/FinSight.Tests/Import/`: content sniffing, row limits, and full card and account numbers never stored from CSV or OFX files.
- `server/FinSight.Tests/Api/ApiTests.cs` and `DataApiTests.cs`: user isolation and deletion.
- `server/FinSight.Tests/Api/GoogleAuthApiTests.cs` and `OnboardingApiTests.cs`: the OAuth flow, Gmail linking and Gemini key handling.
