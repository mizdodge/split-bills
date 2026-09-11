# SplitBill server setup

This package is a clean, framework-dependent ASP.NET Core 8 release. It does not contain the current server's SQLite database, receipt photos, backups, logs, Data Protection keys, or AI API key. A fresh installation creates its own runtime data on first startup.

## Prerequisites

- 64-bit Windows Server or Windows desktop with IIS.
- IIS Web Server, IIS Management Console, and IIS scripting tools.
- IIS Common HTTP Features: Static Content, Default Document, and HTTP Errors.
- IIS Request Filtering.
- Microsoft .NET 8 ASP.NET Core Hosting Bundle x64. This installs the ASP.NET Core runtime and `AspNetCoreModuleV2`.
- An unused TCP port, such as `8080`, and firewall access for the intended LAN/Tailscale clients.

SQL Server is not required. SplitBill uses SQLite at `publish/App_Data/splitbill.db`.

## Fresh install

1. Extract the ZIP to a directory such as `C:\Apps\SplitBill`. Confirm that `C:\Apps\SplitBill\publish\web.config` exists.
2. Open Windows PowerShell as Administrator.
3. Run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
cd C:\Apps\SplitBill
.\setup-iis.ps1 -SiteName SplitBill -Port 8080 -BindAddress '*'
```

If the Hosting Bundle is not installed, download the official .NET 8 Hosting Bundle installer and pass its local path:

```powershell
.\setup-iis.ps1 -SiteName SplitBill -Port 8080 -BindAddress '*' `
  -HostingBundleInstaller C:\Installers\dotnet-hosting-8.x-win.exe
```

The script creates a dedicated `SplitBill` app pool using No Managed Code, Integrated pipeline, 64-bit worker process, ApplicationPoolIdentity, and Load User Profile enabled. It grants Read & Execute to the app and Modify only to `publish\App_Data`, creates the IIS binding, and adds an inbound firewall rule for non-loopback bindings.

The script writes setup logs under `publish\App_Data\setup-logs`. The application writes its SQLite database, receipt files, and Data Protection keys under the same `App_Data` tree.

## First startup

Browse to `http://localhost:8080` or the server's reachable LAN/Tailscale address. The first startup creates the schema and application roles, but creates **no user accounts**. From the publish directory print the one-time bootstrap code:

```powershell
dotnet .\Splitbill.dll --print-bootstrap-code
```

Open `/setup`, enter that code, and create the first Admin account. The code expires after 24 hours and is deleted once setup succeeds. Then sign in as Admin and create the required Moderator/Member accounts from **Manage users**. Existing installations that already have users are detected as setup-complete and keep their users/passwords.

Configure the AI provider from **AI Settings** while signed in as Admin. Enter the new server's OpenAI or Azure OpenAI key there. Azure also needs its endpoint. The key is encrypted using the destination machine's Data Protection key ring.

## Network and HTTPS

`-BindAddress '*'` listens on localhost, LAN, and Tailscale addresses that route to the server. Use a specific address when you need a narrower binding. LAN addresses may change with DHCP. For internet-facing use, bind a hostname, install an HTTPS certificate, and restrict access with firewall/network ACLs.

Browser Push API and service workers require a secure context. `http://localhost:8080` is useful for local testing, but raw IP origins cannot enable production push notifications. The app keeps those HTTP routes available for ordinary use; open a trusted HTTPS hostname when enabling browser notifications.

The preferred private route is Tailscale Serve in front of the existing loopback IIS binding. Run the following from the Tailscale user context that owns the existing Serve configuration:

```powershell
tailscale serve --bg http://127.0.0.1:8080
tailscale serve status
```

Use the HTTPS hostname shown by `tailscale serve status`, then sign in as the transaction-owning Moderator/Admin and enable notifications from **Notifications**. SplitBill trusts forwarded HTTPS metadata only from the loopback proxy. If Tailscale is already managed by another user/session, inspect its status there; do not stop, restart, or kill the existing Tailscale process just to configure this feature.

Preserve `publish\App_Data\data-protection-keys` and the SQLite database during updates. They protect saved AI credentials, VAPID private key material, and browser subscription keys.

## Backup and restore

An Admin can open **System tools** and enter the current password to download a no-store ZIP backup. The package contains a consistent SQLite snapshot, receipt/payment-proof files, the Data Protection key ring, and a checksum manifest. Uploading a ZIP in the same page only validates and stages it; it never replaces the live database from a web request.

Run the packaged elevated handoff from the server after stopping other maintenance work:

```powershell
.\restore-splitbill.ps1 -BackupZip C:\Backups\SplitBill-Backup.zip -PublishPath C:\Apps\SplitBill\publish -SiteName SplitBill
```

The script verifies every manifest checksum and the installation ID for a same-install restore, creates a dated `restore-backups` rollback copy, uses `app_offline.htm` plus the IIS app pool, restores SQLite/files/key ring, and starts the pool only after validation. For a different Windows server use migration mode:

```powershell
.\restore-splitbill.ps1 -BackupZip C:\Backups\SplitBill-Backup.zip -PublishPath C:\Apps\SplitBill\publish -SiteName SplitBill -Migration
```

Migration deliberately does not copy the old machine-bound DPAPI key ring, clears encrypted AI/SharePoint/Web Push material, and requires those settings to be entered again. It preserves users, transactions, receipt photos, payment proofs, and reports. If the script fails during the handoff it restores the dated `App_Data` rollback copy and leaves the maintenance marker cleanup to its guarded `finally` path.

## Validation checklist

1. `dotnet --list-runtimes` includes `Microsoft.AspNetCore.App 8.x`.
2. Login page returns HTTP 200.
3. `publish\App_Data\splitbill.db` and `data-protection-keys` are created.
4. Admin can change password and open AI Settings.
5. Provider model loading works after a valid key is entered.
6. Receipt upload, AI review, quantity-group split, payment proof/approval, Notifications, Reports, and protected image viewers work.
7. Food Pickup Rotation can use Weighted Random or Round Robin and selects only eligible registered participants in the transaction.
8. A fresh export shows populated Summary, Payment Details, Pivot per Person, and Pickup Rotation sheets before enabling Excel editing.
9. Delete any disposable Draft/Unpaid smoke-test transaction with an Admin account.

## Upgrade and rollback

Before upgrading an existing installation, copy the entire destination `publish\App_Data` directory to a dated backup. Place `app_offline.htm` in the active publish directory or stop the app pool, replace binaries/static assets, and leave destination `App_Data` and `appsettings.json` intact. Remove the offline file, recycle the pool, and run the validation checklist.

To roll back, stop the pool, restore the previous binaries and their matching `App_Data` backup, then restart. Do not mix a post-upgrade database backup with older binaries without checking compatibility.

For the packaged upgrade workflow, run `update-iis.ps1` from the package root in elevated PowerShell:

```powershell
.\update-iis.ps1 -SiteName SplitBill
```

It discovers the IIS physical path, creates a dated sibling backup, uses `app_offline.htm`, replaces only binaries/static assets, leaves destination `App_Data` and `appsettings.json` untouched, reapplies App_Data permissions, and starts only the SplitBill app pool. Use `-TargetPublishPath` for a nonstandard site path, `-BackupRoot` for another backup volume, or `-WhatIf` to preview resolved paths. Tailscale is not changed by this script.

## AI key migration note

The encrypted AI value is intentionally not portable. The Data Protection key ring is protected by Windows machine-scope DPAPI, so copying an old database/key directory to another machine does not provide a supported way to recover that key. Re-enter the AI key through Admin AI Settings on the destination server.
