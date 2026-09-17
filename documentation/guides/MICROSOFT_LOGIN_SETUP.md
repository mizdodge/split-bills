# Microsoft Entra Login and Integration Setup

This guide covers the unified Microsoft Integration settings page. It uses the existing SharePoint Microsoft Entra App Registration; do not create a second registration or change tenant permissions as part of this application setup.

## Shared credentials and migration

1. Open **Admin → Microsoft Integration** and enter the existing registration's Directory (tenant) ID, Application (client) ID, and Secret Value.
2. Save the credentials. The client secret is encrypted in the database with SplitBill's persistent DPAPI-backed Data Protection key ring and is never rendered, logged, or written to `appsettings.json`.
3. Existing installations can use **Migrate existing SharePoint credentials**. Migration runs server-side, re-protects the legacy secret under the Microsoft Integration purpose, preserves the SharePoint site/list, enable state, outbox, users, bills, roles, and passwords, and does not require re-entry on the same machine.
4. A secret rotation is shared by SSO and SharePoint. Test both features after restart before retiring the old secret. A credential change requires restart to activate the pending revision; local login remains available.

Do not copy `App_Data`, the SQLite database, or DPAPI keys between machines. A cross-machine reset clears machine-bound credentials and requires re-entry; verified local user records remain intact.

## Step-by-step: enable SSO on the existing Entra registration

### 1. Prepare the application address

1. Open SplitBill using the HTTPS address users will actually use, for example `https://splitbill.example.com`.
2. Sign in locally as Admin and open **Microsoft Integration → Sign-in / SSO** (`/admin/microsoft?tab=sso`).
3. Copy the displayed **Callback URL**. For this version it is `https://splitbill.example.com/account/microsoft/oauth-callback`.
4. Check the copied host, port (if non-default), scheme, and path. The page builds this URL from the current request; opening settings through an HTTP LAN address produces an HTTP example, not a production-ready callback.

Production callbacks require HTTPS; the localhost development exception does not apply to `http://100.x.x.x:8080` or a LAN IP. Users must be able to reach the approved address from their browser. This guide does not change IIS, certificates, DNS, or Tailscale automatically.

### 2. Open the existing application

1. Open [Microsoft Entra admin center](https://entra.microsoft.com/).
2. Select the correct organization/directory.
3. Open **Entra ID → App registrations → All applications** and select the registration already used by SplitBill SharePoint.
4. In **Overview**, compare **Application (client) ID** and **Directory (tenant) ID** with SplitBill. Do not use the application's Object ID as the Client ID.
5. Keep the single-tenant account type. Existing company users do not need new Microsoft accounts specifically for SplitBill.

### 3. Register the Web callback

1. Open **Authentication** in that registration.
2. Under platform configuration, select **Add a platform → Web**, or edit the existing **Web** platform.
3. Add the exact callback copied in step 1 and save. Keep other valid callback entries used by the application.
4. Do not select SPA or mobile/desktop for this server application.
5. Leave implicit grant **Access tokens** and **ID tokens** unchecked; this application exchanges an authorization code on the server. Keep **Allow public client flows** disabled.
6. A front-channel logout URL is not required for this integration. SplitBill logout is local and does not sign users out of all Microsoft applications.

Use `/account/microsoft/oauth-callback`, not `/signin-microsoft`, `/signin-oidc`, or `/account/microsoft-signin/callback`. The last path is the application's post-authentication action, not the registered OAuth middleware callback.

### 4. Configure sign-in permissions and consent

1. Open **API permissions → Add a permission → Microsoft Graph → Delegated permissions**.
2. Find the OpenID permissions and add `openid`, `profile`, and `email` if they are not already present.
3. If organizational policy requires administrator approval, ask the tenant administrator to review and **Grant admin consent**. A non-admin may see this action disabled.
4. Preserve the existing **Application** permissions for SharePoint, including `Sites.Selected` and its separate site grant. Delegated sign-in permissions and application SharePoint permissions can coexist in this registration.

The current login requests `openid profile email`; it does not need `User.Read.All`, `Directory.Read.All`, or `offline_access`. Existing SharePoint consent does not by itself verify interactive login consent. See Microsoft's [scope documentation](https://learn.microsoft.com/en-us/entra/identity-platform/scopes-oidc).

### 5. Check who is allowed to sign in

1. Open **Entra ID → Enterprise applications → All applications** and find the same application (compare the Application ID).
2. Open **Properties** and verify sign-in is enabled for users.
3. Check **Assignment required?** If **Yes**, open **Users and groups → Add user/group**, select the intended users, and assign them. Individual assignment is sufficient; group-based assignment depends on licensing.
4. If assignment is not required, tenant policy still applies. Microsoft access does not create a SplitBill account or grant a SplitBill role automatically.
5. Keep existing MFA and Conditional Access rules; users complete any Microsoft prompts during sign-in.

### 6. Reuse the client secret and enable SplitBill SSO

1. In the registration, open **Certificates & secrets** and check that the existing client secret has not expired.
2. Reuse the stored secret if it is valid. If replacing it, copy the new **Value**, not **Secret ID**, immediately after creation; Microsoft does not display that value again.
3. In SplitBill **Microsoft Integration → Sign-in / SSO**, confirm Tenant ID and Client ID. If shared credentials are already stored, leave the secret blank to retain them for the same IDs.
4. If offered for a legacy installation, use **Migrate existing SharePoint credentials** instead of retyping the secret.
5. Enable the Microsoft login/SSO switch and save. Follow the application's restart requirement after credential changes; retain a local Admin session until verification is complete.
6. In **Manage users**, ensure each intended person has an active local SplitBill account with the correct, distinct company email. Existing local roles and bill history remain attached to that account. The current matching rules are documented below; they include username-prefix fallback, so avoid ambiguous usernames/emails.

Never paste a secret into documentation, screenshots, chat, or `appsettings.json`. Changing this shared secret affects SharePoint too; verify both services after rotation.

### 7. Test the complete login flow

1. Keep the local Admin session open and launch a private/incognito browser window.
2. Visit the same approved HTTPS SplitBill address and select **Login with Microsoft**.
3. Sign in with an intended company user, complete MFA/consent if prompted, and confirm the browser returns to SplitBill.
4. Confirm the correct local display name, role, and existing bills are shown. Log out and repeat to verify subsequent sign-in.
5. Confirm local username/password login still works. In the SharePoint tab, test the existing connection to ensure the shared configuration still works.

A successful SharePoint connection or saved settings does not prove SSO works. Record success only after the browser callback and correct local account have been checked.

### Troubleshooting

| Symptom | Check |
| --- | --- |
| `AADSTS50011` / redirect URI mismatch | Compare the actual request callback with the saved **Web** URI: HTTPS, host, port, path and case. Start from the approved host. See [Microsoft redirect URI rules](https://learn.microsoft.com/en-us/entra/identity-platform/reply-url). |
| Invalid/expired client secret | Confirm the secret **Value**, expiry, tenant/client pair, and active server configuration after restart. Do not remove the old valid secret before testing its replacement. |
| Need admin approval | Tenant admin reviews delegated consent and organizational user-consent policy. |
| User is not assigned | Check Enterprise application assignment when **Assignment required** is enabled. |
| Account not registered in SplitBill | Check the local active account and the matching rules below. Entra membership alone does not create a local account. |
| Login button missing or login returns immediately | Check the SSO switch and stored shared credentials. |
| Correlation failure or callback returns to login | Retry from a fresh private window on one HTTPS host; check cookie handling and the approved reverse proxy's scheme/host forwarding. |
| SharePoint works but SSO fails | Recheck Web callback, delegated consent, user assignment, and local matching; app-only Graph authentication is a different flow. |

For tenant-admin investigation, provide the error code, timestamp, correlation/request ID and callback URL, never the secret or tokens.

## Entra application prerequisites (reference)

Use the existing single-tenant App Registration with a **Web** platform. Preserve current Microsoft Graph application permissions, admin consent, selected-site grants, and SharePoint destinations. For sign-in, add the exact approved callback URI in Azure Entra under **Authentication → Redirect URIs**:

```text
https://<approved-host>/account/microsoft/oauth-callback
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
