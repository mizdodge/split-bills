# SplitBill Project Guide

## 1. Product purpose

SplitBill is a small internal web application for turning receipt photos into trackable split bills. Admin and Moderator users can upload a receipt, review AI-extracted items, assign the bill to participants, and mark each participant as paid. Admin can view and manage every transaction, while a Moderator can only view and manage transactions they uploaded. Admin also manages the Microsoft Entra/SharePoint destination and SplitBill user accounts from the Admin section.

The application intentionally excludes QRIS, bank transfers, reminders, multi-currency settlement, and automatic payment verification. It supports read-only guest links and currency-specific payment obligations with reporting conversions; it does not perform currency exchange or settlement.

## 2. Main user flow

```text
Admin or Moderator login
  -> upload 1-5 ordered raster receipt photos (maximum 10 MB each, 30 MB combined)
  -> application decodes, auto-orients, strips metadata, and stores a protected canonical JPEG
  -> configured AI provider extracts structured receipt JSON
  -> user reviews and edits merchant, date, items, and totals
  -> user enters participant names
  -> choose equal split or assign each item to one or more participants
  -> ASP.NET calculates final amounts and proportional tax/service adjustments
  -> participant list starts as Unpaid
  -> uploader optionally turns on per-transaction pickup; server selects one pickup person from eligible registered participants using Weighted Random or Round Robin after save
  -> user marks individual participants Paid
  -> transaction status automatically becomes Unpaid, Partial, or Paid
  -> Admin/Moderator view the report permitted by their role
```

AI never decides the final monetary split. It converts all ordered images for one receipt into one structured result. The prompt treats the images as pages or sections of the same receipt and tells the model not to duplicate overlapping lines. The deterministic C# calculator owns totals, allocation, and rounding.

If AI fails and the Draft still has a zero grand total, Review and Details show **Proses ulang dengan AI**. The protected POST action is available to Admin or the owning Moderator, reuses every stored image that still exists, creates a new processing log, and preserves manual data on failure.

Receipt review is intentionally forgiving. Merchant, date, quantity, unit price, subtotal, discount, tax, and service may be left blank. The server removes empty rows, defaults a missing quantity to `1`, derives item totals and receipt totals when possible, and creates a single `Total tagihan` item when only Grand Total is known. Step 2 is blocked only when no positive total can be determined, a monetary value is negative, or an entered number cannot be parsed; every remaining error is shown in the form summary.

Receipt item quantity is a positive whole number in the AI contract, Review form, and Split allocation-group editor (`step="1"`). Monetary inputs remain decimal-capable (`step="0.01"`) because receipt prices and adjustments may legitimately include fractional values. `InvariantFormValueProviderFactory` parses standards-based HTML numbers with invariant culture, while display formatting remains `id-ID`; this prevents a value such as `10000.50` from being misread as `1000050`.

Every additional fee or discount is a dynamic charge row with a custom printed label, positive IDR amount, and add/subtract operation. Examples include PB1, PPN, service charge, packaging, delivery, and vouchers. Users can add, edit, or remove rows in Review. Percentage labels are calculated for display as `amount / subtotal * 100`, shown underneath each label, and never used to change the amount. If only a printed percentage is visible with no nominal, AI omits that charge and requests manual review.

## 3. Installation, users, and authorization

The application creates its SQLite database and roles on first startup. Fresh installations intentionally create **no user accounts**. The first administrator is created once at `/setup`: run `dotnet Splitbill.dll --print-bootstrap-code` from the publish directory, enter the printed one-time code, and choose the Admin username/password. The verifier is hashed and the printable copy is Data Protection protected; after setup it is cleared, the Admin is signed in, and the app opens the dashboard. Existing installations that already contain users are automatically marked setup-complete and keep every account and password unchanged.

After the first Admin signs in, use **Manage users** to add Moderators and Members. The database never silently recreates the old development roster.

Legacy accounts in an upgraded database remain valid and can be managed normally. SplitBill does not ship default usernames or passwords. Passwords are hashed by ASP.NET Core Identity, and Administrators create or maintain accounts through **Manage users**.

Authorization is enforced server-side. Hiding a navigation item is only a presentation detail. Transaction queries also apply ownership filtering, so a Moderator cannot retrieve another Moderator's transaction by changing an ID in the URL.

## 4. Technology

- ASP.NET Core 8 MVC and Razor Views
- ASP.NET Core Identity with cookie authentication
- Entity Framework Core 8
- SQLite for zero-configuration local installation
- `IHttpClientFactory` for OpenAI and Azure OpenAI calls
- ASP.NET Core Data Protection for AI, SharePoint, bootstrap, and Web Push secret encryption
- Magick.NET Q8 for bounded raster decoding and canonical image normalization
- Bootstrap 5 plus application CSS and vanilla JavaScript
- xUnit for unit tests

The application uses `Database.EnsureCreatedAsync()` for automatic first-run setup. `DatabaseSchemaUpdater` applies guarded, idempotent additive SQLite DDL for newer receipt, payment, notification, installation, SharePoint, Web Push, and pickup-rotation entities/columns so existing installations upgrade without recreating their database. Legacy image and tax/service/discount values are projected through compatibility fallbacks when an old transaction is opened. If schema evolution becomes frequent, switch to checked-in EF migrations and call `Database.MigrateAsync()` during deployment.

## 5. Repository structure

```text
Splitbill/
|-- Controllers/
|   |-- AccountController.cs          Login, logout, access denied
|   |-- DashboardController.cs        Role-scoped overview
|   |-- TransactionsController.cs     Upload through Paid workflow
|   |-- ReportsController.cs          Admin/global and Moderator/owned reports
|   |-- AdminSettingsController.cs    AI provider settings and connection test
|   |-- AdminSharePointController.cs  SharePoint site/list discovery settings
|   |-- AdminUsersController.cs       Admin user/account management
|   |-- AdminFoodPickupController.cs  Admin pickup eligibility and draw history
|   |-- AdminSystemController.cs      Backup download and restore staging
|   `-- SetupController.cs            One-time first-admin bootstrap
|-- Data/
|   |-- ApplicationDbContext.cs       EF mappings and DbSets
|   |-- DatabaseSeeder.cs             Roles and installation state
|   `-- DatabaseSchemaUpdater.cs      Additive SQLite schema guards
|-- Models/
|   |-- ApplicationUser.cs            Identity user extension
|   |-- DomainModels.cs               Database entities and enums
|   `-- AiReceiptModels.cs            Stable AI output contract
|-- Services/
|   |-- AiReceiptService.cs           Provider/endpoint adapter
|   |-- AiModelCatalogService.cs      Provider model discovery and response parser
|   |-- AiResponseParser.cs           Responses and Chat Completions parser
|   |-- ReceiptReviewNormalizer.cs   Flexible review defaults, charge summaries, and rate hints
|   |-- SplitBillCalculator.cs        Pure deterministic calculation
|   |-- TransactionAccessService.cs   Admin/global and Moderator/owner management rule
|   |-- TransactionStatusService.cs   Draft/Unpaid/Partial/Paid calculation
|   |-- SharePointGraphService.cs     Entra token and Graph site/list discovery
|   |-- SharePointTestStateProtector.cs Short-lived tested list state
|   |-- SharePointNotificationService.cs Localized outbox event creation
|   |-- SharePointNotificationProcessor.cs Durable Graph list delivery/retry
|   |-- FoodPickupRotationService.cs  Secure weighted pickup selection and audit
|   |-- TransactionShareExportService.cs  Safe localized long-JPEG projection
|   |-- AdminUserService.cs           Identity administration and audit rules
|   |-- UploadedImageProcessor.cs     Safe raster decode and canonical JPEG output
|   |-- InstallationSetupService.cs   One-time bootstrap verifier
|   `-- BackupService.cs              Consistent snapshot and restore package validation
|-- ViewModels/                        Form and page-specific models
|-- Views/                             Razor pages grouped by controller
|-- wwwroot/css/site.css               Responsive visual system
|-- wwwroot/js/site.js                 Upload, editor, assignment, settings UI
|-- wwwroot/js/pwa.js                  PWA registration and install prompt
|-- wwwroot/push-service-worker.js     Single root worker for push and offline shell
|-- wwwroot/manifest.webmanifest       PWA metadata
|-- App_Data/
|   |-- splitbill.db                   Auto-created local database (runtime)
|   `-- receipts/                      Protected receipt images (runtime)
|-- Tests/                             xUnit project
|-- Program.cs                         Dependency injection and middleware
|-- appsettings.json                   Connection string, logging, and allowed hosts only
|-- restore-splitbill.ps1              Elevated restore and rollback handoff
|-- Splitbill.csproj                   Authoritative semantic application version
|-- CHANGELOG.md                       Public release history
|-- documentation/
|   |-- README.md                      Documentation index
|   |-- SECURITY.md                    Security and disclosure policy
|   |-- guides/                        IIS and SharePoint operating guides
|   `-- screenshots/                   Sanitized README visual previews
`-- project_guide.md                  This document
```

## 6. Data model

```text
AspNetUsers (username, nullable email, normalized email, password/Identity metadata)

InstallationStates (singleton installation id, one-time bootstrap verifier and setup completion)

AiConfigurations

SharePointConfigurations (singleton destination, encrypted client secret, tested Site/List IDs)

SharePointNotificationOutbox (durable localized Graph/Teams delivery events)

WebPushConfigurations
|-- WebPushSubscriptions (encrypted browser capabilities per user/device)
`-- WebPushDeliveries (durable payment-submitted delivery rows)

AdminUserAuditLogs (sanitized Admin account-management history)

FoodPickupConfigurations (singleton rotation switch and selection strategy)
|-- FoodPickupEligibleUsers (current eligible registered accounts)
|-- FoodPickupAssignments (one current winner per transaction)
`-- FoodPickupDrawHistories (append-only draw audit snapshots)

BillTransaction (header)
|-- TransactionReceiptImage (ordered protected images)
|-- TransactionItem (receipt lines)
|   `-- ParticipantItemAllocation
|-- TransactionCharge (named nominal fee or discount)
|-- TransactionParticipant (amount per person)
|   |-- ParticipantItemAllocation
|   |-- ParticipantAccountLink
|   |-- PaymentApproval (proof image and approve/reject decision)
|   `-- PaymentHistory
|-- UserNotification
`-- ReceiptProcessingLog
```

`ApplicationUser` inherits the standard `Email` and `NormalizedEmail` fields from ASP.NET Identity, so user email is stored directly in `AspNetUsers` without a duplicate custom column. Email remains nullable for compatibility with existing seeded accounts. Startup checks both columns and the normalized-email index idempotently, allowing older installations to upgrade without recreating the user table.

`SharePointConfiguration` is a singleton additive table. It stores normalized Tenant/Client IDs, the SharePoint Site URL and stable Graph Site/List IDs, display metadata, connection-test state, and a Data Protection ciphertext for the Microsoft Entra client secret. The plaintext secret, OAuth token, and tested list payload are never stored in `appsettings.json`, HTML, logs, or the database. A short-lived protected test-state token binds a successful list discovery to the current Admin session/configuration fingerprint; Save rejects stale or forged list selections. `AdminUserAuditLog` records only the Admin actor, target user, action, non-secret changed-field summary, and UTC timestamp.

`SharePointNotificationOutbox` is the durable boundary between payment actions and Power Automate. When SharePoint integration is enabled, saving a split creates one `BillAssigned` event for each registered participant with an email; submitting **I've paid** creates `PaymentApprovalRequested` for the transaction uploader; rejecting that request creates `PaymentRejected` for the linked member and includes the rejection reason; each actual pickup winner change creates one stable `FoodPickupSelected` event for the selected account. Guests and accounts without email are skipped. The business change and outbox row are committed together, while `SharePointNotificationDispatcher` publishes the row independently every 15 seconds and retries transient failures with exponential backoff up to eight attempts. This prevents Microsoft 365 availability from breaking receipt, split, payment, or pickup operations.

`TransactionShareExportService` is the privacy boundary for sharing. It consumes calculator-owned values and returns localized display strings only; no account IDs, emails, usernames, file paths, receipt photos, payment proofs, secrets, rejection notes, or raw money values reach the browser. The exact uploader or an Admin can request the full transaction projection, while a linked Member can request only their own projection. Its `Shared with` label is emitted only for an item share below one unit, naming other fractional owners of that item; whole-unit shares have no sharing label. The local Canvas renderer creates one measured 1000px-wide JPEG whose height grows for every participant and falls back to download when native file sharing is unavailable.

`FoodPickupRotationService` owns the optional pickup feature. Admin enables the rotation, chooses **Weighted Random** or **Round Robin**, and checks eligible registered accounts at `/AdminFoodPickup`. On a successful Step 3 save, candidates are the intersection of that set and the transaction's active registered participants; guests and absent eligible accounts never enter the selection. Weighted Random uses `1 / (1 + current pickup count)` weights with a cryptographically secure random source. Round Robin deterministically chooses a candidate who has never picked up, then the candidate whose current completed pickup assignment is oldest; stable display-name/user-ID ordering resolves an exact tie. An absent account is skipped without receiving an assignment, and a selected account moves to the back because its `SelectedAt` becomes newest. Existing transaction winners remain stable when Admin changes the configured strategy. Assignments and immutable history store the strategy used; Weighted Random stores its normalized probability while Round Robin stores a deterministic `1.0` result. Editing keeps a winner when possible, automatically selects again once when the winner is removed, and records every change in `FoodPickupDrawHistories`. Only Admin can reroll a saved transaction and must provide a bounded reason. Reports and Excel show filter-scoped participation/pickup ratios; these descriptive metrics are separate from the global selection history used by future choices.

The Graph writer deliberately targets the existing Power Automate list contract: `Title`, `Email`, `Description`, and `IsProcessed`. New rows always start with `IsProcessed = false`. Power Automate sends `Description` to `Email`, then updates the same row to processed only after Teams succeeds. Notification title, body, field labels, and currency formatting use the ID/EN culture active in the web request that created the event. Bodies HTML-encode every dynamic value and convert line breaks to explicit `<br>` tags because the Teams connector collapses plain single newlines. Each new message also appends one encoded clickable anchor in `Description`: bill assignments and rejections target `/MyBills/Details/{participantId}`, while payment requests target `/Payments/Approvals`. `BillTransaction.NotificationBaseUrl` captures the uploader's safe scheme/host/port/path base when Step 3 is saved, so later member events keep using an address the uploader selected. This keeps merchant, transaction, amount, actor, rejection reason, and the action link distinct without allowing receipt/user text to inject markup. The app stores richer event, transaction, participant, amount, recipient, actor, retry, and diagnostic metadata locally, so the SharePoint list can remain minimal.

### BillTransaction

The header stores the transaction number, merchant, receipt date, upload timestamp, uploader, subtotal, discount, tax, service, grand total, protected receipt filename, AI confidence/warnings, split method, aggregate status, the additive `RequiresFoodPickup` choice (false by default), and the safe notification base URL captured at split save time.

`TransactionReceiptImages` stores each protected filename, MIME type, and order. `BillTransaction.ReceiptImagePath` remains the first-image compatibility field so transactions created before multi-image support continue to render. Existing SQLite installations receive the detail table through idempotent startup DDL.

`TransactionCharges` stores the editable printed label, nominal amount, add/subtract operation, and display order. Header discount/tax/service fields remain aggregate compatibility values; arbitrary added fees are included in the service/other summary, while labels containing tax/pajak/PPN/PB1 contribute to the tax summary. The printed Grand Total remains the final split amount.

### TransactionParticipant and PaymentHistory

The participant stores the calculated amount and current payment state. Every transition to Paid creates a separate history row containing the old state, new state, actor, and timestamp. Reports use participant amounts to calculate paid and outstanding totals.

### Money and dates

Monetary values use `decimal(18,2)`, never floating-point types. UI formatting follows Indonesian culture. Receipt dates use `DateOnly`; audit timestamps use `DateTimeOffset` and are stored in UTC, then shown in local server time.

## 7. AI integration

Admin chooses one active provider and one API mode:

- OpenAI + `/responses`
- OpenAI + `/chat/completions`
- Azure OpenAI + `/responses`
- Azure OpenAI + `/chat/completions`

OpenAI and Azure OpenAI credentials are managed only through the Admin AI Settings page. The API key field is shared by the selected provider, is never rendered back to the browser, and may be left blank when keeping the stored key for the same provider. Switching provider requires its key. The server encrypts the key with ASP.NET Core Data Protection before storing it in `AiConfigurations.ProtectedApiKey`, and decrypts it only while constructing the provider request. The persistent key ring lives under protected runtime storage at `App_Data/data-protection-keys`, uses the stable application name `SplitBill`, and is protected with machine-scope Windows DPAPI so IIS worker restarts do not invalidate saved credentials. Neither provider reads an API key from `appsettings.json` or environment configuration during normal operation.

The September 2026 rollout includes a one-time compatibility migration: when an existing OpenAI deployment still has `OpenAI:ApiKey` in its live configuration and its active database row has no encrypted key, startup encrypts that value into the database. Deployment then removes the legacy file setting. New installations never require that setting.

The Model field is a provider-backed dropdown. `AdminSettings/Models` runs server-side and calls OpenAI `GET /v1/models` or Azure OpenAI `GET /openai/v1/models` / `GET /openai/models?api-version=...`. Provider credentials never appear in the returned browser payload. Azure still requires its deployment name because inference calls target the deployed resource name rather than the base model ID.

The AI must return this stable application contract:

```json
{
  "schemaVersion": "2.0",
  "merchantName": "Solaria",
  "transactionDate": "2026-09-03",
  "currency": "IDR",
  "items": [
    {
      "lineNumber": 1,
      "name": "Nasi Goreng",
      "quantity": 2,
      "unitPrice": 35000,
      "totalPrice": 70000,
      "confidence": 0.98
    }
  ],
  "subtotal": 70000,
  "charges": [
    { "label": "PB1", "amount": 7000, "operation": "add" },
    { "label": "Service charge", "amount": 3500, "operation": "add" }
  ],
  "grandTotal": 80500,
  "confidence": 0.96,
  "needsReview": false,
  "warnings": []
}
```

`AiReceiptService` builds provider-specific requests and converts connection failures into `AiServiceException`. `AiResponseParser` hides response-envelope differences from the rest of the application. A failed AI call is logged, and the receipt remains available for manual review rather than being lost.

When adding another provider, implement the transport and keep returning `ReceiptAiResult`. Controllers and views should not contain provider-specific branching.

## 8. Split rules

### Equal split

Grand total is divided among all unique, non-empty participant names. Whole-IDR amounts are used. Any rounding remainder is assigned to the final participant so that the sum always equals the transaction grand total.

### Item split

Every item must have at least one participant. Shared items are divided evenly among selected participants. The difference between the item total and grand total represents discounts, tax, service, and receipt adjustments; it is distributed proportionally to each participant's item amount. The final participant receives any rounding remainder.

### Member bill transparency

`ISplitBillCalculator.BuildParticipantBreakdowns` derives the member view from the persisted item allocations, receipt charges, and final `TransactionParticipant.Amount`. It returns typed item lines (quantity/share, full receipt line amount, member amount) and typed signed adjustment lines (original charge label and amount, member allocation, residual or rounding kind). Explicit charges plus any unitemized Grand Total difference are allocated across participants using stable item-base weights; deterministic rounding and a reconciliation line make every member equation exact without rewriting stored participant amounts. `/MyBills` only shows two item previews, while `/MyBills/Details/{id}` owns the complete breakdown and private receipt gallery.

`/Transactions/Details/{id}` reuses the same typed breakdowns for Admin and the owning Moderator. Each payment row has a native expandable order panel showing that participant's item quantity shares, item subtotal, allocated discounts/fees with effective receipt percentages, adjustment total, and final participant amount. The full receipt detail remains separate so operators can compare participant allocations against the original receipt without duplicating financial calculations in Razor or JavaScript.

Once any participant is Paid, the split can no longer be edited. This protects payment history from being detached from changed amounts.

Admin can permanently delete Draft or Unpaid transactions from the list or detail screen after confirmation; Moderator deletion remains limited to their own Draft. The POST action rechecks role, ownership, status, and payment/audit activity inside the database transaction, then removes the complete relational graph and protected receipt/proof files. Partial and Paid transactions remain protected.

## 9. Receipt storage and security

Receipt photos can contain sensitive information. They are stored under `App_Data/receipts`, outside the public web root. Admin/Moderator transaction screens load an ordered image through `Transactions/ReceiptImage/{id}?index=0`. A linked Member details page uses the separate `MyBills/ReceiptImage/{participantId}?index=0` route; it checks the participant account link before resolving the stored filename, applies the ordered multi-image/legacy fallback, allowlists the image content type, and emits `nosniff` with range support. Neither route accepts a browser-supplied filename or exposes receipt files from `wwwroot`.

Uploads accept one to five common raster images, 10 MB per image, and 30 MB combined. The shared Magick.NET processor bounds decoded dimensions (12,000px per side / 40MP), ignores the browser MIME/extension, auto-orients EXIF, strips metadata, flattens transparency to white, and emits a static JPEG with a GUID filename. The shipped Windows native build was verified for JPEG/JFIF, PNG, WebP, GIF, BMP, and TIFF; HEIC/HEIF and AVIF use the same decoder boundary when an optional ImageMagick delegate is installed, otherwise they receive a clear unsupported-format error. Animated/multi-page input uses its first frame/page; SVG/SVGZ, PDF/PS/EPS, PSD/XCF, RAW, icon/cursor, and unknown formats are rejected. Generated filenames never reuse a client-provided path. The endpoint takes only `Path.GetFileName()` from stored data before resolving the physical path.

The single root `push-service-worker.js` also provides a PWA offline shell. Only versioned static assets and the neutral offline page are cached. Authenticated HTML, APIs, receipt photos, and payment proofs always use the network and are never placed in Cache Storage. `manifest.webmanifest` and `pwa.js` provide install metadata and the browser install prompt without creating a second worker that could break Web Push.

Admin **System tools** creates a consistent SQLite `VACUUM INTO` snapshot and a ZIP manifest containing checksummed database, protected files, and the Data Protection key ring. Backup downloads are no-store and require the current Admin password. Restore upload only stages and validates a ZIP; `restore-splitbill.ps1` performs the elevated `app_offline.htm`/app-pool handoff, creates a rollback copy of `App_Data`, checks every manifest hash and same-install `InstallationId`, and supports `-Migration`, which deliberately omits the old DPAPI key ring and runs the package's `--reset-machine-secrets` against the target data to disable and clear AI, SharePoint, and Web Push machine-bound material for re-entry on the new server.

Forms that mutate state use antiforgery validation. Management operations allow Admin globally and re-check transaction ownership for Moderator on the server.

Admin SharePoint settings use `ISharePointGraphService` for Microsoft Entra client-credentials authentication, site resolution, and list discovery. The Admin supplies one HTTPS `*.sharepoint.com` Site URL; after Test Connection succeeds, the application resolves the Site ID and automatically populates a list dropdown from that site. The selected IDs and encrypted client secret are saved only after a short-lived protected test state confirms that the posted values and list came from the current Admin's latest successful test. Failed token requests expose only bounded diagnostic codes/messages; raw Entra/Graph bodies, secrets, and tokens are never logged or rendered. Once enabled, the durable notification dispatcher writes the configured list for bill assignment, payment approval request, rejection, and pickup-winner events. Use Application permissions with admin consent and `Sites.Selected` plus an explicit selected-site grant where possible; see [`SHAREPOINT_SETUP.md`](documentation/guides/SHAREPOINT_SETUP.md).

Manage Users uses ASP.NET Core Identity through `IAdminUserService`. Admin can create accounts, edit display name/email/role, reset passwords, and enable/disable login. Usernames remain immutable, normalized emails are unique at the service boundary, and every account has exactly one of the seeded `Admin`, `Moderator`, or `Member` roles. Accounts are never hard-deleted because transaction and payment history refer to their IDs. Admin cannot disable or demote the currently signed-in Admin, and the last enabled Admin cannot be removed. Disable and password reset update the security stamp so existing sessions expire under the configured Identity validation interval. `AdminUserAuditLogs` records sanitized account-management actions without passwords, tokens, secrets, or complete user payloads.

## 10. Screens and routes

| Route | Role | Purpose |
| --- | --- | --- |
| `/account/login` | Anonymous | Sign in |
| `/setup` | Anonymous, fresh installation only | One-time first Admin setup |
| `/Dashboard` | Authenticated | Admin/global or Moderator/owned operational analytics; Member personal monthly spending and recent bills |
| `/Transactions` | Admin, Moderator | Role-scoped transaction list |
| `/Transactions/Upload` | Admin, Moderator | Upload receipt |
| `/Transactions/Review/{id}` | Admin or owning Moderator | Correct AI result |
| `POST /Transactions/Reprocess/{id}` | Admin or owning Moderator | Retry AI for a zero-total Draft with stored photos |
| `/Transactions/Split/{id}` | Admin or owning Moderator | Define participants and split |
| `/Transactions/Details/{id}` | Admin or owning Moderator | Payment actions plus expandable per-participant item and adjustment breakdowns |
| `POST /Transactions/Delete/{id}` | Admin or owning Moderator | Admin deletes Draft/Unpaid; owning Moderator deletes Draft only, with relational/private-file cleanup |
| `/Reports` | Admin, Moderator, Member | Role-scoped report with optional date/status/uploader filters, pickup rotation statistics, and winner names; a Member sees only shared transactions containing their account, with every participant in those bills |
| `/Reports/Details/{id}` | Admin, Moderator, Member | Dedicated read-only report detail with people, menu, and payment status; Member access requires participation in that transaction |
| `/Reports/Export` | Admin, Moderator, Member | Date/filter-scoped Excel workbook with summary, payment details, typed-value Pivot per Person, and a populated Pickup Rotation sheet |
| `/MyBills` | Member | Bills assigned to the signed-in participant |
| `/MyBills/Details/{id}` | Member | Transparent item/charge breakdown, private receipt gallery, and **I've paid** claim action |
| `GET /Transactions/ShareData/{id}` | Admin or exact uploader | Localized privacy-safe full-transaction sharing projection |
| `GET /MyBills/ShareData/{id}` | Linked Member | Localized privacy-safe own-bill sharing projection |
| `/MyBills/ReceiptImage/{participantId}?index=0` | Linked Member | Authorized ordered receipt image for that member's bill |
| `/Payments/Approvals` | Admin, Moderator | Pending Member payment claims; Moderator is limited to owned transactions |
| `/Notifications` | Authenticated | Bill/payment notifications with read and read-all actions |
| `/account/change-password` | Authenticated | Change password with Identity validation |
| `/AdminSettings` | Admin | AI configuration |
| `/AdminSettings/Models` | Admin | Server-side provider model discovery |
| `/AdminSharePoint` | Admin | Enter Entra credentials and a SharePoint Site URL, test the connection, auto-populate lists, and save a destination |
| `POST /AdminSharePoint/TestConnection` | Admin | Resolve the site through Microsoft Graph and return selectable lists |
| `POST /AdminSharePoint/Save` | Admin | Persist the tested Site/List IDs and encrypted client secret |
| `/AdminFoodPickup` | Admin | Enable rotation, select eligible accounts, view current pickup/participation counts, and review draw history |
| `POST /AdminFoodPickup/Save` | Admin | Replace the eligible account set and rotation switch through one antiforgery-protected request |
| `/AdminUsers` | Admin | Search/filter Identity users and view role, email, and access state |
| `/AdminUsers/Create` | Admin | Create an internal account with one application role |
| `/AdminUsers/Edit/{id}` | Admin | Edit display name/email/role and reset a password |
| `POST /AdminUsers/Enable|Disable/{id}` | Admin | Enable or disable login and revoke the existing security stamp |
| `/AdminSystem` | Admin | Download a verified backup or stage a restore package |

The UI has a desktop sidebar and a responsive mobile shell. Mobile keeps a visible **Keluar** action in the header, shows notifications beside the profile, and exposes the relevant workflow tabs: Dashboard, Upload, Transactions, Reports, Member bills, moderator approvals, and Admin AI Settings. SharePoint Integration, Manage Users, Food Pickup Rotation, and System Tools remain reachable through the Admin mobile overflow menu so the bottom bar stays usable. Upload/Review/Split activate only the Upload tab; transaction list/detail activate only Transactions. The primary path remains upload, review, split, and Paid. Empty valid validation summaries are hidden, while actual error messages stay next to the action that needs correction. The core palette is Lime `#CDFF70`, Emerald `#003A40`, Stone Grey `#444547`, and Cool White `#F2F0FA`.

Dashboard analytics are calculated on the server. Admin sees all permitted transactions and Moderator sees only transactions they uploaded. Their shared operational dashboard shows six-month non-draft bill value, the five largest outstanding participant balances, the oldest outstanding transaction, the most frequent merchants, and average full-settlement duration, plus the existing totals and recent activity. A pure Member receives a separate personal dashboard derived only from `ParticipantAccountLink` rows for the signed-in account: current and previous month totals, lifetime paid/outstanding amounts, a six-month paid/outstanding history, and direct links to that Member's recent bill details. Drafts never contribute to analytics; awaiting confirmation remains outstanding until an authorized approver confirms it.

## 11. Local setup

Requirements: .NET 8 SDK.

```powershell
dotnet restore
dotnet run
```

Open the HTTPS or HTTP URL printed by ASP.NET. The database, receipt directory, payment-proof directory, and Data Protection key ring are created automatically. On a fresh install run `dotnet Splitbill.dll --print-bootstrap-code` once from the publish directory, open `/setup`, and create the first Admin. Existing databases with users skip this flow.

After startup, log in as Admin and enter the provider API key, model/mode, and any Azure endpoint/deployment fields on AI Settings. Use **Simpan & test** to validate the saved connection. The key is encrypted in the database and is not written to application configuration files.

## 12. Testing

Run all tests from the repository root:

```powershell
dotnet test
```

Unit and integration-style tests cover exact equal-split totals, whole-quantity allocation groups, shared promo bundles, proportional adjustments, invalid allocations, aggregate payment states, approval/proof authorization, both AI response envelopes, multi-image request construction and limits, model discovery, receipt normalization, SharePoint/Web Push outboxes, Weighted Random and Round Robin pickup behavior, backup/restore validation, localization parity, and MVC authorization/form validation. Add tests to the pure service layer whenever a financial rule changes.

`DraftDeletionTests` also exercises the controller against an isolated in-memory SQLite database with foreign keys enabled and temporary receipt files. It verifies dependent-data cleanup, Admin/owner access, cross-owner rejection, non-Draft preservation, missing receipts, missing transactions, and the Step 3 account-picker query against SQLite's DateTimeOffset behavior. It never uses the live database.

Before releasing, also manually verify the first-Admin setup on a clean database, each role boundary, ownership isolation, an AI failure followed by manual entry, equal split, shared-item split, payment approval/rejection, report filters and Excel output, pickup selection/reroll, protected image viewers, and mobile layout.

## 12.1 IIS deployment

For a clean server package, run the release builder from the repository root:

```powershell
.\build-release.ps1
```

It runs the Release build and tests, publishes to a validated staging directory, excludes runtime data/secrets, generates `VERSION.txt` and a SHA-256 manifest, and creates a semantic-versioned timestamped ZIP under `artifacts/`. The `<Version>` in `Splitbill.csproj` is the single release-version source used by assembly metadata, the runtime **Update Center**, HTTP integration user agents, and package naming. Git release tags use the matching `vMAJOR.MINOR.PATCH` form. The ZIP contains `publish/`, setup/update/restore scripts, the root README/project guide, and the `documentation/` directory. It never reads the current live database into the package.

For a direct local publish during development, use:

```powershell
dotnet publish Splitbill.csproj -c Release -o publish
```

IIS must point its website **Physical path** to the generated `publish` directory, not the source directory and not directly to `Splitbill.dll`. Publishing transforms `web.config`, which configures `AspNetCoreModuleV2` to start `Splitbill.dll` and raises IIS request filtering to 32 MiB so a valid 30 MB multi-image upload plus multipart overhead is accepted.

Required IIS setup:

1. Install the .NET 8 ASP.NET Core Hosting Bundle on the IIS server.
2. Create an application pool, for example `SplitBill`, with **.NET CLR Version: No Managed Code**.
3. Add a website with physical path `L:\Data Programming\GLM\Splitbill\publish` when deploying on the current machine.
4. Choose an unused HTTP port such as `8080`, or configure the intended host name and HTTPS certificate.
5. Grant the application-pool identity Modify permission only to `publish\App_Data` so SQLite and protected receipts can be created and updated.

Example permission command when the pool is named `SplitBill`:

```powershell
icacls "L:\Data Programming\GLM\Splitbill\publish\App_Data" /grant "IIS AppPool\SplitBill:(OI)(CI)M" /T
```

Open the site at its configured binding, for example `http://localhost:8080`. Startup creates the SQLite database and roles. On a fresh installation, print the one-time bootstrap code and create the first Admin at `/setup`; no office-user roster is seeded automatically.

To listen on localhost, LAN, and Tailscale together, run the setup script from Windows PowerShell as Administrator:

```powershell
.\setup-iis.ps1 -Port 8080 -BindAddress '*'
```

This sets the site's HTTP binding to `*:8080:` and allows inbound TCP 8080 on all local addresses and network profiles. Existing hostless bindings on that port are replaced by the wildcard; other ports and host-specific bindings are preserved. Use `http://localhost:8080` on this server, or the server's current LAN/Tailscale IPv4 address followed by `:8080` from reachable devices. LAN addresses can change with DHCP. Passing a specific `-BindAddress` retains the narrower setup option.

Example endpoints after IIS setup:

| Network | URL |
| --- | --- |
| This server | `http://localhost:8080` |
| Private overlay network | `http://100.64.0.10:8080` |
| LAN | `http://192.168.1.50:8080` |

Replace the example addresses with the server's current addresses. Devices accessing LAN or a private overlay network must have a route to that network; verify access from a separate device.

For either provider, enter the API key through the Admin AI Settings screen after IIS starts. Keep the IIS application-pool user profile enabled so ASP.NET Core Data Protection can persist the key ring required to decrypt saved keys.

Browser Push API and service workers require a secure context. `http://localhost:8080` is useful for local testing, but raw IP origins cannot enable production push notifications. The app keeps HTTP routes available for ordinary use; open a trusted HTTPS hostname when enabling browser notifications.

The preferred private route is Tailscale Serve in front of the existing loopback IIS binding. Run the following from the Tailscale user context that owns the existing Serve configuration:

```powershell
tailscale serve --bg http://127.0.0.1:8080
tailscale serve status
```

Use the HTTPS hostname shown by `tailscale serve status`, then sign in as the transaction-owning Moderator/Admin and enable notifications from **Notifications**. The app trusts forwarded HTTPS metadata only from the loopback proxy, so direct HTTP bindings keep their existing behavior. If Tailscale is already managed by another user/session, inspect its status there; do not stop, restart, or kill the existing Tailscale process just to configure this feature.

When a browser subscription is enabled, the endpoint and encryption keys are protected with the persistent DPAPI-backed Data Protection key ring. Preserve `publish\App_Data\data-protection-keys` and the SQLite database during updates or existing subscriptions cannot be decrypted.

To upgrade an existing IIS installation from a package root, run `.\update-iis.ps1 -SiteName SplitBill` in elevated PowerShell. The script backs up the destination, uses `app_offline.htm`, replaces only binaries/static assets, leaves `App_Data` and `appsettings.json` intact, reapplies permissions, and starts the SplitBill app pool. Tailscale is not changed by this script.

## 13. Extension rules for future coding

1. Update `CHANGELOG.md` for user-visible release changes.
2. Keep provider-specific request details inside `Services/AiReceiptService.cs`.
3. Keep financial math inside `ISplitBillCalculator`; do not calculate money in JavaScript or Razor.
4. Apply transaction ownership filters before loading protected data.
5. Never place receipt images or unencrypted secrets under `wwwroot`.
6. Preserve the `ReceiptAiResult` contract or version it explicitly.
7. Add or update tests whenever split, rounding, parsing, or payment-state behavior changes.
8. Update this guide when routes, roles, core entities, configuration, or flows change.

## 14. Natural next features

Useful follow-up work includes pagination for large datasets, background AI processing, richer audit exports, scheduled payment reminders, formal EF migrations, broader browser/end-to-end tests, and optional observability/health dashboards. SharePoint/Teams delivery, browser push, analytics, PWA installation, backup/restore, and pickup rotation are already implemented.

## 15. Current vNext implementation

The approved vNext scope is implemented in source: Member accounts and registered participant links, guest participants, quantity allocation groups, two-step payment approval with proof images, in-app and optional browser notifications, password changes, dedicated reports, role-specific analytics dashboards, SharePoint/Teams delivery, Food Pickup Rotation, PWA installation, Admin backup/restore tools, complete `id-ID`/`en-US` localization, and route-aware responsive navigation. Database upgrades are additive and run automatically at startup, so an existing SQLite installation keeps its transactions, receipts, proofs, settings, and protected Data Protection keys.

Payment states are `Unpaid`, `AwaitingConfirmation`, and `Paid`. A linked Member submits a proof-backed claim from My Bills; the transaction owner or Admin confirms or rejects it with a visible reason. A Moderator/Admin can mark a participant Paid directly and can reopen Paid back to Unpaid. Each transition creates typed history and the appropriate notification. Excel exports contain Summary, Payment Details, Pivot per Person, and Pickup Rotation sheets; visible totals and pickup metrics are calculated on the server and written as typed cells, so the workbook remains populated without depending on Excel recalculation.

Member report transparency uses a two-stage authorization boundary. The database query first selects only transactions containing a `ParticipantAccountLink` for the signed-in Member. After that transaction boundary is established, the report, detail page, and Excel export project every participant in those shared transactions. This lets coworkers verify each person's menu, amount, and Paid/Awaiting/Unpaid state without exposing unrelated transactions, emails, receipt files, or payment-proof images.

The account picker used by Step 3 performs its SQLite-compatible lockout filtering in memory after loading the small user list; this avoids unsupported `DateTimeOffset` translation while preserving the same active-account behavior. Its hidden participant payload uses ASP.NET Web JSON conventions: camelCase output and case-insensitive input, matching the vanilla JavaScript editor and preserving selected chips after server validation. A successful password change refreshes the authentication cookie and responds with HTTP 303 to `/Dashboard`; the Dashboard controller then renders the operational or personal role-specific experience through a clean GET rather than retaining the password form POST. Notification and approval timestamps are sorted after SQLite materialization because its provider cannot translate `DateTimeOffset` ordering.

Reports open with blank date/status/uploader filters and therefore show every transaction the current role may access, including Draft, Unpaid, Partial, and Paid. Reset returns to that unbounded view. Supplying either date bound filters only that side; supplying both uses an inclusive range. Draft rows remain visible for operational completeness but have no participant payment rows until split is saved, so they do not contribute person-level payment totals. The same optional filters feed Excel, and an unbounded workbook is named `SplitBill_Report_all_all.xlsx`. The transaction-detail table appears before the filter-scoped Food Pickup Rotation statistics, which intentionally render as the final Report section.

## 16. Clean deployment package and Excel correction

The release-hardening work is implemented by `build-release.ps1` and documented in [`SERVER_SETUP.md`](documentation/guides/SERVER_SETUP.md). The generated ZIP is a fresh sanitized package without the current SQLite database, receipts, proofs, backups, logs, Data Protection keys, or credential data. A new server creates its own database and machine-bound key ring on first startup, then the first Admin enters provider credentials through the protected Admin pages.

The Excel `Pivot per Orang` / `Pivot by Person` implementation now writes typed, server-calculated values to Summary and Pivot cells and verifies them by reopening the workbook without calculation. This keeps the person totals visible in Protected View and in spreadsheet viewers that do not recalculate formulas. Older exports still need **Enable Editing** followed by `Ctrl+Alt+F9` as a one-time workaround.

## 17. Receipt, split, payment, and member-detail feedback

The feedback implementation is shipped. Receipt quantities are whole numbers in the Review and Split UI, each item has a visible Row/Baris caption, registered accounts are selected through cards, and Atur Pembagian uses allocation groups. Each group consumes a whole quantity and has either one owner or multiple equal-sharing participants, so a Qty `3` item can contain `Qty 2 → Mizan` plus `Qty 1 → Ragil + Agi`; a full Qty `1` B1G1 row can also be shared. Canonical group composition is stored in the additive nullable `AllocationPlanJson` field while calculated participant allocations remain the financial source. One person can participate across several item rows. My Bills and Report Details show each linked person's item name, quantity/share, and item amount, with an equal-split fallback. Eligible Draft/Unpaid transactions expose Edit until payment/audit activity exists. Admin can permanently delete Draft or Unpaid transactions with their relational graph and private files; Partial/Paid remain protected and Moderator deletion remains limited to their own Draft. Member payment claims require a private proof image retained per approval attempt; Admin and the owning Moderator can inspect it from Approvals/Transaction Details, while the linked member can inspect only their own attempts. Rejection requires a reason tied to the exact approval and displayed to the claimant, and notification cards use distinct accessible semantics per type. Pure Members now have a personal Dashboard plus My Bills, while Admin and Moderator share the role-scoped operational Dashboard.

## 18. Browser push notifications

Opt-in native browser notifications for `PaymentSubmitted` are implemented. After a linked Member submits **I've paid**, only the transaction-owning Moderator/Admin receives the push across active subscribed devices; the existing in-app notification remains authoritative. The implementation uses encrypted Web Push subscriptions, stable protected VAPID keys, a durable delivery outbox, safe Approval Queue deep links, explicit permission UI, logout/expiry controls, a same-device test action, and ID/EN support. A trusted HTTPS origin is required: `http://localhost` may be used for local development, while raw private-network HTTP IP URLs cannot register the production service worker. A private HTTPS reverse proxy can sit in front of the loopback IIS site.
## Guest access and multi-currency

The current application supports original-currency obligations with immutable reporting snapshots. `CurrencyConfiguration` stores the IDR reporting default, Frankfurter-compatible provider settings, up to four dashboard currency codes, and the selected primary dashboard rate; provider secrets use DPAPI/Data Protection. `CurrencyExchangeRate` keeps effective dates and retrieval metadata, while `BillTransaction` stores `CurrencyCode`, `ReportingCurrencyCode`, `ExchangeRateToReporting`, capture mode, source, and optional manual note. Legacy rows are additively backfilled to IDR identity rates. Analytics and Excel normalize only through saved snapshots; payment state remains in the original currency.

Guest participants remain name-only. `GuestAccessLink` stores a SHA-256 token hash plus DPAPI-protected ciphertext for authorized recopy. A participant-scoped token keeps backward compatibility: one active token opens both `/g/my-bill/{token}` (that guest's bill) and `/g/transaction/{token}` (the whole transaction). `GuestTransactionAccessLink` is a separate transaction-scoped token; it opens only `/g/transaction/{token}`, never `/g/my-bill/{token}`, and has an independent revoke/regenerate lifecycle. Both projections are GET-only and read-only. Receipt images are token-scoped and protected by the authorized controller. Guest pages intentionally omit account identifiers, payment proofs, approval notes, secrets, and management controls. The authenticated uploader/Admin manages links from the Share icon beside the transaction status in `/Transactions/Details/{id}`: the Link tamu tab contains the transaction link and guest-only participant links, and both the uploader and Admin get the JPG export tab, including when all participants are guests. Moderators remain limited to their own transactions; registered participants do not receive guest links. Copy/regenerate/revoke use antiforgery POSTs, and regeneration invalidates the previous token. Copy uses the Clipboard API when available, a legacy copy command on private HTTP origins, and a selected read-only URL field if automatic copying is blocked; a token enters the DOM only after an authorized copy request. Guest links use the validated request origin (localhost, LAN, or Tailscale) and never accept a browser-supplied host.

Configure currency under `/AdminCurrency`. Admin/Moderator dashboards retain the transaction-scoped trend section for up to six used foreign currencies. A separate current-rate section at the bottom is independent of transaction history and shows one Admin-selected primary pair as a wide card plus up to three selected secondary pairs. Admin changes that one-to-four selection inline on Dashboard through the protected `POST /Dashboard/SaveCurrencyDashboard`; Moderator sees the shared configuration read-only, and Member keeps the personal dashboard without this operational card. Missing selected pairs are fetched through `ICurrencyRateService`, while all chart points and percentage movement are projected in C#. Each chart uses up to seven distinct latest stored effective dates, not necessarily seven consecutive calendar days. Its headline rate is the newest stored value, and its change percentage compares that value with the preceding stored effective date. The complete scoped read-only rate list is available at `/Dashboard/RateHistory`; only Admin can test, refresh, or change provider settings. The endpoint contract, authentication modes, manual fallback, cache failure behavior, and deployment preservation rules are documented in `documentation/guides/CURRENCY_AND_GUEST_SETUP.md`. IIS updates must preserve `publish/App_Data` and live `appsettings.json`; Tailscale is not part of the deployment operation.
