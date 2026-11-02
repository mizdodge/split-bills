# Personal account settings

Open the gear icon or **Menu → Account settings** (`/account/settings`). Every authenticated account manages only itself. Admin management of other users remains separate.

| Area | Behavior |
| --- | --- |
| Profile | Display name is editable (1–100 characters after trimming, no control characters). Username, email and roles are read-only; ask an Admin to change them. Bill/account IDs, allocation names already saved in historical transactions and permissions are preserved. |
| Language | ID or EN is saved on the account and applied to authenticated requests on every device. The global ID/EN buttons also update this preference. Users without a saved preference retain browser/cookie/default behavior. |
| Teams notifications | Controls future recipient events sent through the Admin-configured SharePoint/Power Automate integration. It neither enables that integration nor changes another user's delivery. |
| Browser notifications | Controls delivery to the account's registered subscriptions; browser permission, HTTPS and the existing role/event eligibility are still required. Notifications links back to Account settings when this preference is off. |
| Local password | Existing local users retain the password-change page and must verify their current password. Passwordless linked users can create a local password after fresh Microsoft verification. |
| Session security | Sign out everywhere requires the current local password, or a fresh Microsoft proof for a passwordless account. It revokes the current session too and disables existing push subscriptions; users sign in and register devices again. |
| Microsoft account | Existing connect, confirmation, disconnect, recovery-admin restriction and local-password fallback rules remain. Creating a local password does not disconnect Microsoft automatically. |

In-app notification history and bills remain accessible when external channels are disabled. Unsent queued deliveries are cancelled and recipient preferences are checked before dispatch; already delivered or in-flight messages cannot be recalled. Re-enabling a channel does not resend cancelled messages. Notification-language generation retains the existing event-language contract; the new language preference controls the user's web interface.

## Fresh Microsoft verification

Use the existing HTTPS origin and Entra configuration described in [Microsoft setup](MICROSOFT_LOGIN_SETUP.md). The same linked tenant/object must reauthenticate; no Microsoft password is entered into SplitBill. A five-minute, single-use request is bound to the current account/security stamp, a hashed browser secret, purpose, link version and credential revision. A recent validated `auth_time` is required. A missing/stale claim, wrong account/browser, expired request, disabled SSO/account, revoked link or changed credentials/stamp invalidates it. Security verification failures return to Account settings with a localized message.

Requests store hashes and metadata, never plaintext passwords, OAuth tokens or the browser binding secret. New passwords follow the configured Identity policy (currently at least six characters, a lowercase letter and a digit). Identity hashes them. Successful local-password creation consumes the request and refreshes the current session; the new security stamp invalidates old sessions. Signing out everywhere consumes its proof and rotates the stamp without creating a replacement session. Cookie stamps are checked on every authenticated request rather than waiting for the usual validation interval. Already-running requests are not retroactively aborted.

## Storage and deployment

Additive startup DDL creates `AccountSecurityRequests`, adds nullable `AspNetUsers.PreferredCulture`, default-on `TeamsNotificationsEnabled` and `BrowserNotificationsEnabled`, and nullable `SharePointNotificationOutbox.RecipientUserId`. Legacy users/data remain intact and existing channels remain on. Self-service profile, preferences, password creation and session revocation create non-secret `AdminUserAuditLog` events attributed to the same user. Roles, usernames and email are never accepted by self-service forms.

Updates must preserve live `App_Data`, the SQLite database, DPAPI key ring and `appsettings.json`. Keep the same Microsoft HTTPS callback registration. The feature does not require SMTP, new Graph permissions, resetting accounts or changing Tailscale.
