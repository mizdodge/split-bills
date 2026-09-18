## 2026-09-17

- Added Microsoft Integration with one encrypted shared Microsoft Entra credential source and independent Sign-in/SSO and SharePoint controls.
- Added verified linking for existing local users while preserving local login, user IDs, roles, bills, and old SharePoint routes.
- Added idempotent migration of existing protected SharePoint credentials without requiring secret re-entry; failures are recorded without exposing plaintext.
- Added setup documentation, localized labels, migration and protection regression tests, and included the Microsoft guide in release packages.
- Release gate: `build-release.ps1` completed successfully; artifact: `artifacts/_batch6-release/SplitBill-v1.0.0-Server-20260917-131929.zip`; 197 tests passed. Real-tenant HTTPS/browser SSO round trip remains unverified because no live tenant activation was available.

All notable public changes to SplitBill are documented here.

## Unreleased

### Added

- Dashboard perspective switch for Admin and Moderator: a compact segmented selector above the heading lets elevated roles toggle between their operational overview (`?view=operations`, default) and their own personal spending view (`?view=personal`). The personal view shows all non-Draft bills linked to the user's `ParticipantAccountLink`, including bills uploaded by others and bills predating any role promotion. Members always see the personal view with no selector. Switching is a bookmarkable `GET` request; no schema change, cookie, or role change is introduced.

- Account settings gear in desktop/mobile navigation, Microsoft connection status and password-confirmed disconnect; passwordless users must obtain a local password before unlinking.

- Unified Microsoft & SharePoint Integration Admin interface (`/admin/microsoft`) providing a consolidated 2-tab layout (`tab=sso` and `tab=sharepoint`), copyable callback URL helper, and live Outbox metrics.
- Microsoft OpenID Connect code flow with PKCE, standard token validation and no token persistence.
- Explicit, password-confirmed Microsoft account linking with browser-bound single-use confirmation; no email/username auto-linking.
- Optional default-off registration of new Microsoft users as Members, with email-prefix usernames and collision rejection; recovery username admin remains local-only.
- Visual `TempData["ErrorMessage"]` and `TempData["StatusMessage"]` alert banners on `Login.cshtml`.
- Explicit `Cache-Control` headers for static files in `Program.cs`: `no-cache, no-store, must-revalidate` for `push-service-worker.js` and `public, max-age=31536000, immutable` for fingerprinted assets (`?v=...`).

### Changed

- Upgraded PWA Service Worker (`push-service-worker.js`, cache version `splitbill-static-v3`) from Cache-First to Network-First with cache fallback for static stylesheets and scripts, eliminating stale CSS on deployed updates without requiring browser hard refresh.
- Consolidated navigation menus in both desktop sidebar and mobile bottom nav: removed duplicate SharePoint link, routing administrators through the unified **Microsoft Integration** page.
- Refined integration tab styling to a clean segmented pill control with signature SplitBill Lime (`var(--primary)` `#CDFF70`) active highlight and deep emerald (`#003A40`) typography.

### Fixed

- Resolved HTTP 500 startup crash caused by unconfigured OAuth client options via dynamic `MicrosoftOAuthNamedOptions`.
- Fixed sign-in callback redirect loop where users were returned to login unauthenticated due to missing token claims.
- Added automatic fallback in `AdminSharePointController` to reuse shared Microsoft credentials when no independent SharePoint secret is specified.
- Maintained backward compatibility for `/AdminSharePoint` with an automatic HTTP 302 redirect to `/admin/microsoft?tab=sharepoint`.
- Constrained desktop left sidebar to viewport height with an isolated vertical scroll container for navigation links, preventing long menus from clipping the user profile, password change link, logout button, and version.
- Added a short-height desktop fallback (`max-height: 520px`) enabling full-sidebar scroll when viewport height is too short for static headers and footers.
- Replaced diagonal arrow logout glyphs with a recognizable door-and-outward-arrow SVG across desktop and mobile shells with a minimum 44px hit target.
- Replaced diamond glyphs with an outline bell SVG for Notifications navigation with inverted active badge coloring and zero clipping.

### Documentation

- Updated `project_guide.md`, `design.md`, `README.md`, and `MICROSOFT_LOGIN_SETUP.md` with the unified 2-tab integration model, Service Worker Network-First caching, and 205-test release gate.

## 1.0.0 - 2026-09-15

First public release.

### Included

- Multi-image receipt upload with bounded server-side normalization.
- OpenAI and Azure OpenAI receipt extraction through Responses or Chat Completions.
- Equal and quantity-group item splitting, including shared portions and promo bundles.
- Admin, Moderator, and Member access scopes with first-Admin bootstrap.
- Member payment-proof submission and two-step approval with rejection reasons.
- Transparent per-person item, discount, fee, percentage, and rounding breakdowns.
- Role-aware dashboards, detailed reports, and four-sheet Excel exports.
- Weighted Random and deterministic Round Robin food-pickup rotation.
- Optional per-transaction pickup with surprise-only winner reveal and preserved history.
- Privacy-scoped All, Selected, Individual, and Member long-JPEG sharing with guest-link management.
- Optional SharePoint List outbox integration for Power Automate and Teams messages.
- Responsive Indonesian and English UI, installable PWA shell, private media viewers, backup, restore, and IIS update tooling.

### Security and distribution

- Runtime databases, receipts, payment proofs, backups, and Data Protection keys are excluded from source control and release packages.
- AI and Microsoft Entra secrets are encrypted at rest and entered only through Admin settings.
- Fresh installations contain no predefined user accounts or default passwords.
