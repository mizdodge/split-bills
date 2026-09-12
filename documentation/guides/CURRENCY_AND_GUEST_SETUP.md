# Currency and guest-link setup

SplitBill keeps `IDR` as the default reporting currency. Each bill keeps its original receipt currency for the actual payment obligation and stores an immutable conversion snapshot for dashboards, reports, and Excel.

## Currency provider

The built-in provider is [Frankfurter v2](https://frankfurter.dev/) at `https://api.frankfurter.dev`. It needs no API key. An Admin can open **Currency settings** and configure a Frankfurter-compatible endpoint:

```text
GET {BaseUrl}/v2/currencies
GET {BaseUrl}/v2/rates?base=USD&quotes=IDR&date=YYYY-MM-DD
```

Supported authentication modes are None, Bearer, and X-Api-Key. Secrets are encrypted with the existing Windows DPAPI/Data Protection key ring and never go into `appsettings.json` or exports. Public HTTP endpoints require explicit private-endpoint opt-in. The response must use Frankfurter's direction: one unit of the base currency equals the returned number of quote-currency units.

The Admin can test the endpoint, refresh cached rates, and enable/disable the daily refresh worker. A provider outage keeps the last successful cache. A foreign receipt cannot silently use a `1:1` rate; Review must show a cached/remote rate or a positive manual rate with a source note. Weekend dates use the provider's previous effective business date, which remains visible in the saved snapshot.

Admin and Moderator dashboards show up to six foreign currencies used by their scoped transactions. Each card plots the latest seven available rates, displays the current rate and previous-rate percentage change, and marks stale data. **View full history** opens `/Dashboard/RateHistory`, a paged read-only list in the configured reporting currency; Admin-only configuration and refresh actions remain under `/AdminCurrency`.

Both the transaction-scoped trends and the current-rate cards use **up to seven distinct latest effective dates stored in `CurrencyExchangeRates` per currency pair**, not a fixed seven-calendar-day window. A weekend, holiday, or missing refresh can make the displayed date range longer than a week; a new pair may have fewer than seven points. The headline rate is the latest stored point, and the percentage compares that point with the preceding stored effective date, which is not necessarily yesterday. The daily refresh worker can add new points even without transactions; an empty selected pair is also fetched when the operational dashboard loads. The date labels and stale state show the limits of available history.

The bottom of the Admin/Moderator dashboard also contains **Current currency rates**, which does not depend on transaction history. It displays one primary wide card and up to three secondary cards. Admin can open **Filter currencies**, select one to four supported base currencies, choose exactly one as the primary rate, and apply the shared dashboard preference. Moderator sees the same selection read-only. The default selection is USD, SGD, EUR, and JPY against IDR, with USD as the primary pair. If a selected pair has no cached value, the dashboard requests it through the configured provider; if only the configured primary pair is unavailable, an available selected pair fills the wide card temporarily. Provider failure leaves an informative unavailable state without breaking the dashboard.

Currency selection is based on the provider catalog intersected with the checked-in ISO metadata. Zero- and two-minor-unit currencies are supported. Three-minor-unit currencies remain unavailable while the application's money contract is `decimal(18,2)`.

## Guest links

Guests are entered by name only. After Step 3, every participant without a registered account receives one active bearer link. The uploader sees **Copy guest link**, **Regenerate**, and **Revoke** in that participant's Transaction Details row.

The copied URL opens `/g/my-bill/{token}`. The page contains a switch to `/g/transaction/{token}` using the same token. My Bill shows the guest's allocation; Whole Transaction shows participant display names, orders, amounts, statuses, receipt details, pickup result, and currency snapshot. Both views are read-only and hide usernames, emails, account IDs, payment proofs, approval notes, and management actions.

On HTTPS or localhost, the copy button uses the browser Clipboard API. On an HTTP private-network address, where that API may be unavailable, it tries the browser's legacy copy command. If the browser blocks both, the requested URL appears in a selected read-only field for manual copying. That field is created only after the authorized POST succeeds; the guest token is never embedded in the initial page HTML. A server-side link failure shows a separate message.

Tokens are random 32-byte Base64URL values. Only a SHA-256 hash and DPAPI-protected ciphertext are stored. Tokens are never logged, exported, sent to SharePoint/Power Automate, or embedded in initial HTML. Links remain active until revoked, regenerated, or deleted with an allowed transaction. Guest pages and receipt responses use `no-store`/`no-referrer` headers and reject cross-transaction image access.

## Deployment notes

When updating IIS, preserve `publish/App_Data` (SQLite database, receipt images, payment proofs, push subscriptions, and DPAPI keys) and the live `appsettings.json`. Use the packaged `update-iis.ps1` flow with a rollback backup. A migrated installation must retain its existing IDR rows and snapshots; no data is revalued when rates refresh.
