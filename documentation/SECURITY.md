# Security Policy

## Supported versions

Security fixes are applied to the latest code on the default branch. Older snapshots and deployment packages may no longer receive fixes.

| Version | Supported |
| --- | --- |
| Latest tagged release | Yes |
| Latest default branch | Yes |
| Older releases or forks | No guaranteed support |

## Reporting a vulnerability

Please do **not** open a public issue for a suspected vulnerability or include sensitive data in discussions, screenshots, logs, or pull requests.

Use the repository's **Security → Report a vulnerability** option to submit a private GitHub security advisory. Include:

- A concise description and the affected route or component
- Steps to reproduce or a minimal proof of concept
- The security impact and required user role
- The tested commit or release
- Suggested mitigation, if known

If private vulnerability reporting has not been enabled for the repository, contact the repository owner privately and ask for a secure reporting channel. Do not send real API keys, Microsoft Entra secrets, access tokens, receipts, payment proofs, cookies, or production databases.

The maintainer should acknowledge a complete report when practical, verify its impact, prepare a fix, and coordinate disclosure after supported deployments can be updated. Reports may be closed when they cannot be reproduced, have no security impact, or concern an unsupported fork.

## Sensitive deployment data

SplitBill stores sensitive runtime material outside `wwwroot`:

- `App_Data/splitbill.db`
- `App_Data/receipts/`
- `App_Data/payment-proofs/`
- `App_Data/data-protection-keys/`
- Runtime backups and logs

Never commit or publish these files. Preserve the database and Data Protection key ring together during deployment; losing or replacing the key ring can make encrypted AI and SharePoint credentials unreadable.

AI API keys and Microsoft Entra client secrets must be entered only through the Admin settings pages. Do not place them in `appsettings.json`, source files, issue reports, build artifacts, or screenshots. Revoke and rotate a credential immediately if exposure is suspected.

Microsoft Integration uses one DPAPI-protected shared Entra client secret. The SSO and SharePoint switches are independent, and SharePoint can continue using its legacy ciphertext when shared mode is off. Existing SharePoint ciphertext is migrated server-side only: it is decrypted under the old purpose and immediately re-encrypted under the dedicated Microsoft purpose; plaintext is never returned to the browser, persisted, or logged. SSO does not provision accounts or trust mutable email claims. It validates the tenant/object identity and signs in only an enabled existing user with a matching verified link. Account linking requires an authenticated local session, fresh local verification, short-lived single-use state, security-stamp/browser/config-revision binding, duplicate-link rejection, and explicit unlink/revocation handling.

OAuth callback activation requires a real HTTPS origin registered in the reused Entra App Registration (localhost is suitable only for local development). A successful client-credential/SharePoint settings test is not proof that interactive SSO works; verify a complete real-tenant browser round trip before enabling SSO in production. This repository's offline test/release gate does not claim that live round trip.

## Deployment expectations

- Run the application on supported Windows hosts because machine-scope DPAPI is enforced at startup.
- Use HTTPS before exposing SplitBill outside a trusted private network.
- Create the first Admin through the one-time bootstrap flow with a strong password; rotate any legacy seeded credentials that remain on an upgraded installation.
- Restrict Microsoft Graph access to the intended SharePoint site; prefer `Sites.Selected` with the required site-level role.
- Give the IIS application-pool identity Modify access only where required, especially `App_Data`.
- Keep receipts and payment proofs behind their authorized controller endpoints.
- Apply updates using the documented backup and `app_offline.htm` handoff so live data and encryption keys remain intact.

## Security boundaries

The AI extracts receipt content but does not calculate final monetary allocations. Authorization, money calculations, status changes, protected-file access, and payment approval rules are enforced server-side. A model response or hidden UI control is never treated as an authorization decision.
