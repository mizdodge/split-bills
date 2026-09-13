# SplitBill documentation

This directory contains the operational guides, security policy, visual previews, and historical implementation plans for SplitBill.

The authoritative product and architecture reference is [`project_guide.md`](../project_guide.md). The public project overview is the root [`README.md`](../README.md), and release-level changes are recorded in [`CHANGELOG.md`](../CHANGELOG.md).

## Guides

- [`SERVER_SETUP.md`](guides/SERVER_SETUP.md) — Windows Server, IIS, first Admin bootstrap, updates, backup, restore, rollback, and cross-server migration
- [`SHAREPOINT_SETUP.md`](guides/SHAREPOINT_SETUP.md) — Microsoft Entra permissions, SharePoint List schema, site grant, Power Automate, Teams, and troubleshooting
- [`SECURITY.md`](SECURITY.md) — supported versions, private vulnerability reporting, secret handling, and deployment boundaries

Share exports are generated locally from server-projected strings: the uploader or Admin can export the full transaction, and a Member can export only their own bill. The JPEG is a single dynamically sized 1000px Canvas image; raw HTTP automatically downloads it when native file sharing is unavailable.

## Visual previews

The [`screenshots`](screenshots/) directory contains sanitized, illustrative UI previews used by the root README. They do not contain live database content, credentials, addresses, or protected receipt/payment files. [`share-transaction-long.jpg`](screenshots/share-transaction-long.jpg) is a 1000 × 4394 output painted directly by the production `transaction-share.js` Canvas renderer; it demonstrates that all ten participant blocks and the final receipt total remain inside one JPEG.

The application-screen source is [`readme-gallery.html`](screenshots/source/readme-gallery.html). Open it with `?view=dashboard`, `?view=upload`, `?view=split`, `?view=transaction`, `?view=mybill`, or `?view=report` to render a specific 1600×900 preview. The long-share source is [`share-export-preview.html`](screenshots/source/share-export-preview.html); it loads the real renderer from `wwwroot/js/transaction-share.js` and paints sanitized precomputed display values. The Dashboard, Upload Receipt, and Set Split previews mirror the current Razor hierarchy, labels, dynamic states, navigation, and responsive application styles; all sample values are sanitized.

Release-level changes are summarized in the public [`CHANGELOG.md`](../CHANGELOG.md). Internal implementation plans and environment-specific operational logs are intentionally kept outside the public repository.
# SplitBill documentation

Public setup and operational guides:

- [Currency and guest-link setup](guides/CURRENCY_AND_GUEST_SETUP.md)
- The currency guide explains why dashboard charts show up to seven **stored rate dates**, which may cover more than seven calendar days, and how the Admin chooses the current-rate cards.
- [SharePoint setup](guides/SHAREPOINT_SETUP.md)
- [IIS setup and deployment](guides/SERVER_SETUP.md)

Internal implementation plans stay outside release packages.
