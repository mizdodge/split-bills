# Microsoft Entra Login and Integration Setup

This guide covers the unified Microsoft Integration settings page. It uses the existing SharePoint Microsoft Entra App Registration; do not create a second registration or change tenant permissions as part of this application setup.

## Shared credentials and migration

1. Open **Admin → Microsoft Integration** and enter the existing registration's Directory (tenant) ID, Application (client) ID, and Secret Value.
2. Save the credentials. The client secret is encrypted in the database with SplitBill's persistent DPAPI-backed Data Protection key ring and is never rendered, logged, or written to `appsettings.json`.
3. Existing installations can use **Migrate existing SharePoint credentials**. Migration runs server-side, re-protects the legacy secret under the Microsoft Integration purpose, preserves the SharePoint site/list, enable state, outbox, users, bills, roles, and passwords, and does not require re-entry on the same machine.
4. A secret rotation is shared by SSO and SharePoint. Test both features after restart before retiring the old secret. A credential change requires restart to activate the pending revision; local login remains available.

Do not copy `App_Data`, the SQLite database, or DPAPI keys between machines. A cross-machine reset clears machine-bound credentials and requires re-entry; verified local user records remain intact.

## Entra application prerequisites

Use the existing single-tenant App Registration with a **Web** platform. Preserve current Microsoft Graph application permissions, admin consent, selected-site grants, and SharePoint destinations. For sign-in, add the exact approved callback URI in Azure Entra under **Authentication → Redirect URIs**:

```text
http(s)://<approved-host>/account/microsoft/oauth-callback
```

> **Tip:** You can copy the exact Callback URL directly from **Admin → Microsoft Integration → Masuk dengan Microsoft (SSO)** using the one-click copy button.

Production Microsoft login should use HTTPS. The application requests only `openid`, `profile`, and `email` for interactive login; directory-wide Graph reads and unnecessary scopes are not used.

Tenant administrators may need to assign users in the Enterprise application and grant consent according to organizational policy. SplitBill does not import Microsoft groups or roles.

## Sign-in / SSO tab

Enable SSO only after shared credentials, redirect URI, user assignment, and consent are ready in Azure.

### User Matching and Sign-In Behavior:
When a user clicks **Login dengan Microsoft**:
1. **Primary Match (Persistent Link):** The system first checks if the verified Microsoft identity (`tenant ID` + `subject/oid`) is already linked to an existing SplitBill user. If linked, the user is signed in immediately.
2. **First-Time Match & Auto-Link:** If the user has not yet been linked by `(tenant, object ID)`, the callback service matches the verified Microsoft email (`email`, `preferred_username`, or `upn`) against existing SplitBill accounts using multi-stage matching:
   - Exact email or normalized email (`u.Email == cleanEmail` / `u.NormalizedEmail`).
   - Exact username equal to the Microsoft email (`u.UserName == cleanEmail`).
   - SplitBill username matching the email prefix before the `@` symbol (for example, a SplitBill user with username `mizan` matching `mizan@glmsystems.com`).
   - Manually designated `MicrosoftAccountEmail` set in Manage Users.
3. **Auto-Link:** Once matched, SplitBill binds the Microsoft `(tenant ID, object ID, email, display name)` to that local account, creates a persistent cookie session via `SignInManager.SignInAsync`, and redirects to `/Dashboard`. Subsequent logins resolve directly via Priority 1.
4. **Unregistered Accounts:** If no matching user exists in the SplitBill database, sign-in is rejected with an explicit alert on the login screen: *"Akun Microsoft ({email}) belum terdaftar di SplitBill. Hubungi administrator."* SplitBill never performs just-in-time (JIT) creation of unknown accounts.
5. **Account Lockout:** If the matched local account is locked out or disabled by an administrator, login is prevented.

## SharePoint tab

The SharePoint tab retains its independent enable switch, site URL, list destination, test state, notification outbox, and existing Graph contracts. It consumes the same active shared credential snapshot but does not require SSO to be enabled. Existing `/AdminSharePoint` bookmarks and POST contracts remain compatibility routes; new navigation uses Microsoft Integration.

Use the existing SharePoint setup guide for `Sites.Selected`, selected-site `write` access, list columns, and Power Automate notification delivery: [SHAREPOINT_SETUP.md](SHAREPOINT_SETUP.md).

## Local recovery and logout

Local login remains available during provider failure, disabled SSO, HTTPS setup work, and credential rotation. The SplitBill password is managed locally; Microsoft passwords and MFA are managed by Microsoft. SplitBill logout clears its application and temporary linking state but does not sign the user out of every Microsoft service. Existing Microsoft-origin sessions follow normal Identity expiry/security-stamp rules and configuration/link checks; this is not continuous directory deactivation synchronization.

## Verification status

Automated schema, protector, SharePoint compatibility, authorization, build, and full test coverage are part of the repository release gate. A real Microsoft round trip is **not** considered verified by a credential or Graph connectivity check. Live SSO remains unverified until an approved HTTPS host, configured existing tenant registration, consent/assignment, and a real test account complete local linking and later Microsoft-only sign-in without changing production data.
