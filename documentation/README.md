# SplitBill documentation

This directory contains the operational guides, security policy, visual previews, and historical implementation plans for SplitBill.

The authoritative product and architecture reference is [`project_guide.md`](../project_guide.md). The public project overview is the root [`README.md`](../README.md), and release-level changes are recorded in [`CHANGELOG.md`](../CHANGELOG.md).

## Guides

- [`SERVER_SETUP.md`](guides/SERVER_SETUP.md) — Windows Server, IIS, first Admin bootstrap, updates, backup, restore, rollback, and cross-server migration
- [`SHAREPOINT_SETUP.md`](guides/SHAREPOINT_SETUP.md) — Microsoft Entra permissions, SharePoint List schema, site grant, Power Automate, Teams, and troubleshooting
- [`SECURITY.md`](SECURITY.md) — supported versions, private vulnerability reporting, secret handling, and deployment boundaries

## Visual previews

The [`screenshots`](screenshots/) directory contains sanitized, illustrative UI previews used by the root README. They do not contain live database content, credentials, addresses, or protected receipt/payment files.

The reproducible source is [`readme-gallery.html`](screenshots/source/readme-gallery.html). Open it with `?view=dashboard`, `?view=upload`, `?view=split`, `?view=transaction`, `?view=mybill`, or `?view=report` to render a specific 1600×900 preview. The Dashboard, Upload Receipt, and Set Split previews mirror the current Razor hierarchy, labels, dynamic states, navigation, and responsive application styles; all sample values are sanitized.

Release-level changes are summarized in the public [`CHANGELOG.md`](../CHANGELOG.md). Internal implementation plans and environment-specific operational logs are intentionally kept outside the public repository.
