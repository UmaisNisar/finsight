# Security policy

FinSight handles bank and credit card statements, so security reports are taken seriously and handled privately.

## Reporting a vulnerability

Please **do not open a public issue, pull request or discussion** for a security problem.

Report it privately through GitHub:

1. Go to the repository's **Security** tab.
2. Choose **Report a vulnerability**.
3. Describe the issue, the affected version or commit, steps to reproduce, and the impact you expect.

Only you and the repository's maintainers can see the report. If you are unsure whether something is a security issue, report it this way anyway.

Please don't include real financial data, statements or credentials in a report. Use the demo mode or synthetic PDFs to reproduce.

## What to expect

FinSight is maintained by one person, so timelines are best effort:

- An acknowledgement, normally within a week.
- An assessment, and a fix or mitigation plan, as soon as the issue is understood. Severe issues come first.
- Credit in the fix's commit or release notes, if you want it.

Please give a reasonable amount of time to fix the issue before you disclose it publicly.

## Scope

In scope: the code in this repository, the API in `server/`, the web app in `web/`, and the CI configuration in `.github/`.

Out of scope:

- Deployments operated by someone else. Their configuration (TLS, reverse proxy, key and database file protection) is the operator's responsibility; see `docs/security-and-privacy.md`.
- Vulnerabilities in Google, Gemini or other third-party services themselves.
- Findings that need physical access to a user's unlocked device, or a compromised browser.
- Denial of service through sheer request volume, and missing best-practice headers without a demonstrated impact.

## Supported versions

Only the latest commit on `main` receives security fixes.

## Testing guidelines

Only test against accounts and data you own, or the demo mode. Don't access other people's data, degrade the service for others, or keep data you come across.

## How FinSight protects data

The verified description of what FinSight stores, encrypts and shares, and its known limitations, is in [docs/security-and-privacy.md](docs/security-and-privacy.md).
