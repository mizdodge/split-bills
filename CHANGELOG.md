# Changelog

All notable public changes to SplitBill are documented here.

## Unreleased

- Added an optional per-transaction pickup switch with surprise-only winner reveal and preserved pickup history.
- Added privacy-scoped Transaction and Member share projections and a two-pass long JPEG Canvas renderer with a 25-participant clipping fixture.
- Added All/Selected/Individual uploader sharing, Member own-bill sharing, Web Share API detection, and automatic download fallback.

## 1.0.0 - 2026-09-11

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
- Optional SharePoint List outbox integration for Power Automate and Teams messages.
- Responsive Indonesian and English UI, installable PWA shell, private media viewers, backup, restore, and IIS update tooling.

### Security and distribution

- Runtime databases, receipts, payment proofs, backups, and Data Protection keys are excluded from source control and release packages.
- AI and Microsoft Entra secrets are encrypted at rest and entered only through Admin settings.
- Fresh installations contain no predefined user accounts or default passwords.
