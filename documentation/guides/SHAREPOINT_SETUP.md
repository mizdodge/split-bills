# SplitBill SharePoint connection setup

> Microsoft Entra credentials are now managed from the unified **Admin → Microsoft Integration → SharePoint** tab. This legacy guide remains valid for site permissions, list preparation, and Power Automate. Existing `/AdminSharePoint` bookmarks remain supported.

## 1. Create the Microsoft Entra app

1. Open **Microsoft Entra admin center → App registrations → New registration**.
2. Choose **Accounts in this organizational directory only** (single tenant).
3. No redirect URI is needed; SplitBill uses an app-only server-to-server connection.
4. Copy the app's **Application (client) ID** and the directory's **Directory (tenant) ID**.
5. Open **Certificates & secrets → New client secret**. Choose an expiry that matches the organization's rotation policy and copy the **Secret Value** immediately. The value is shown only once.

## 2. Grant the least-privilege Graph permission

1. Open **API permissions → Add a permission → Microsoft Graph → Application permissions**.
2. Add `Sites.Selected`.
3. Select **Grant admin consent** and complete the tenant-admin prompt.
4. Grant this app access to the specific target SharePoint site with the `write` role. The site grant must be created by an administrator using an approved Microsoft Graph/SharePoint administration tool. The grant targets the app's client ID and the target site's ID; it is separate from tenant-wide admin consent.

The SplitBill test can read lists only after both steps are complete: tenant admin consent and the selected-site grant. If the organization deliberately accepts broad access, `Sites.ReadWrite.All` can be used instead, but it gives the app access across the tenant and should be reviewed as an exception.

## 3. Configure SplitBill

Sign in as an Admin and open **Admin → Microsoft Integration → SharePoint**. Shared Tenant ID, Client ID, and client secret are entered once above the tabs. Existing installations can migrate their encrypted SharePoint secret without re-entry. The SharePoint feature switch remains independent from Microsoft SSO.

Enter the SharePoint-specific fields:

```text
Tenant ID       Directory (tenant) ID from the app registration
Client ID       Application (client) ID from the app registration
Client Secret   Secret Value copied when the secret was created
Site URL        The exact site URL, for example:
                https://company.sharepoint.com/sites/Office
```

Click **Test connection**. SplitBill obtains an app-only token, resolves the Site URL, and loads the selectable lists in that site. Choose a list and click **Save configuration**. The saved record keeps the stable Site ID and List ID, not only their display names.

The URL must be HTTPS and use a `*.sharepoint.com` host. Do not paste a document URL, list view URL, query string, fragment, username, or password. Use the site root or a site path such as `/sites/Office`.

The client secret is encrypted with the same persistent Windows DPAPI-backed Data Protection key ring used by the rest of SplitBill. It is never rendered again, written to `appsettings.json`, stored in browser storage, or included in logs. Leave the field blank on a later test to reuse the stored secret for the same Tenant ID and Client ID. Enter a new value to rotate it.

## 4. SharePoint list preparation

### 4.1 Create the list

1. Open the SharePoint site saved in SplitBill.
2. Select **Site contents → New → List → Blank list**.
3. Name the list `SplitBill`. Any display name works, but this name keeps the SplitBill and Power Automate screens easy to recognize.
4. Keep the built-in **Title** column.
5. Add the columns in the next table by selecting **+ Add column**. Create them with the exact names shown and without spaces.

SplitBill sends a Microsoft Graph `POST /sites/{site-id}/lists/{list-id}/items` request whose `fields` object uses SharePoint **internal column names**. A matching display name is not sufficient when the internal name differs.

### 4.2 Required columns

| Exact internal name | SharePoint type | Required | Default | Written by |
| --- | --- | --- | --- | --- |
| `Title` | Single line of text | Keep the existing setting | — | SplitBill |
| `Email` | Single line of text | No | Blank | SplitBill |
| `Description` | Multiple lines of text, **plain text** | No | Blank | SplitBill |
| `IsProcessed` | Yes/No | No | **No** | SplitBill creates `No`; Power Automate changes it |

Recommended `Description` configuration:

- Type: **Multiple lines of text**
- Number of lines for editing: any convenient value, for example `6`
- Text type: **Plain text**
- Append changes to existing text: **No**

SplitBill HTML-encodes dynamic receipt/user values and stores explicit `<br>` tags in `Description`. New messages also append one safe clickable HTML link. Bill assignments and rejection messages link directly to the participant's SplitBill detail page; payment-approval messages link to the uploader's Approvals page. The Teams message action renders the line breaks and link. Do not wrap `Description` in a code block or apply another HTML-encoding expression in Power Automate.

The exact Graph payload is intentionally small:

```json
{
  "fields": {
    "Title": "Tagihan baru dari Fifi",
    "Email": "member@example.com",
    "Description": "Halo Member,<br><br>Tagihan baru ...<br><br><a href=\"https://splitbill.example.com/MyBills/Details/123\">Lihat detail tagihan</a>",
    "IsProcessed": false
  }
}
```

### 4.3 Recommended operational columns

These columns are optional for SplitBill but make the list easier to operate. Power Automate owns their values.

| Exact internal name | SharePoint type | Choices/default | Purpose |
| --- | --- | --- | --- |
| `FlowStatus` | Choice | `Pending`, `Sent`, `Failed`; default `Pending` | Human-readable delivery state |
| `FlowProcessedAt` | Date and time | Blank | Time of the successful or final failed attempt |
| `FlowError` | Multiple lines of text, plain text | Blank | Safe failure summary; never store tokens or connector secrets |

A compact **All Items** view can show these columns in order:

```text
Title | Email | IsProcessed | FlowStatus | FlowProcessedAt
```

Keep `Description` available but hide it from the compact view if messages make rows too tall.

### 4.4 Verify the internal names

Creating the columns manually with the exact names above normally produces matching internal names. Columns imported from Excel, renamed after creation, or copied from another list can instead have names such as `field_1` or encoded values.

To verify a column:

1. Open **List settings**.
2. Under **Columns**, select the column.
3. Inspect the browser URL and find the `Field=` value.
4. Confirm the values are exactly `Email`, `Description`, `IsProcessed`, `FlowStatus`, `FlowProcessedAt`, and `FlowError` as applicable.

If a required internal name is wrong, create a replacement column with the exact expected name. Renaming only the display label does not repair an existing internal name.

### 4.5 Give SplitBill write access

List discovery proves that the app can read the site; it does not prove that it can create rows. With the recommended `Sites.Selected` **Application** permission, the service principal also needs an explicit `write` role on the selected site. A `read` grant allows the connection test to work but notification delivery will fail.

The permission has two independent parts:

```text
Entra app: Microsoft Graph → Application → Sites.Selected → Admin consent
SharePoint site: the same Client ID → role write
```

After a permission change, wait a few minutes, run **Test connection** again in SplitBill, select `SplitBill`, enable the integration, and select **Save configuration**.

### 4.6 Build the Power Automate flow

Create an **Automated cloud flow** and use this structure:

```text
When an item is created
        ↓
Send Teams notification
        ├── successful ──→ Mark notification as Sent
        └── failed/timeout → Mark notification as Failed
```

#### Trigger — When an item is created

- Connector: **SharePoint**
- Trigger: **When an item is created**
- Site Address: the same SharePoint site configured in SplitBill
- List Name: `SplitBill`

Use the create-only trigger. **When an item is created or modified** would run again when the flow updates `IsProcessed` and can create a notification loop.

#### Action — Send Teams notification

Rename the action to `Send Teams notification`.

- Connector: **Microsoft Teams**
- Action: **Post message in a chat or channel**
- Post as: **Flow bot**
- Post in: **Chat with Flow bot**
- Recipient: dynamic content **Email** from the trigger
- Message: dynamic content **Description** from the trigger

The link in `Description` uses the same origin that the Admin/Moderator used when saving the split. Have the uploader open SplitBill through an address reachable by recipients (for example the Tailscale or LAN URL). If the uploader saves through `localhost`, the generated link intentionally points to `localhost`, which other devices cannot open.

The recipient email must belong to an account that the selected Teams connection can message. SplitBill skips guests and registered accounts without an email, so they do not create unusable list rows.

#### Success branch — Mark notification as Sent

Add a SharePoint **Update item** action as a parallel branch after the Teams action and rename it `Mark notification as Sent`.

- Site Address/List Name: same as the trigger
- ID: dynamic content **ID** from **When an item is created**
- `IsProcessed`: **Yes**
- `FlowStatus`: **Sent**, when the optional column exists
- `FlowProcessedAt`: expression `utcNow()`, when the optional column exists
- `FlowError`: blank
- **Run after**: only **is successful** for `Send Teams notification`

If the Update item card asks for `Title` or another required field, map that field from the trigger so it is preserved.

#### Failure branch — Mark notification as Failed

Add another SharePoint **Update item** action as the second parallel branch and rename it `Mark notification as Failed`.

- ID: dynamic content **ID** from the trigger
- `IsProcessed`: **No**
- `FlowStatus`: **Failed**, when available
- `FlowProcessedAt`: expression `utcNow()`, when available
- `FlowError`: a safe static message such as `Teams notification failed`; inspect Flow run history for technical details
- **Run after**: **has failed** and **has timed out** for `Send Teams notification`

In the new designer, select the action and configure this under **Settings → Run After**. Power Automate supports success, failure, skipped, and timeout outcomes. Keep the Teams action's normal retry policy for intermittent Microsoft 365 failures.

Do not mark the row as processed before Teams succeeds. Otherwise a failed Teams call appears completed and is harder to recover.

### 4.7 Remove unnecessary actions from an older flow

`When an item is created` already returns the newly created item. The normal flow therefore does not need:

- **Get items** with `ID eq ...`
- **Apply to each** around a single trigger item
- A filter query against `IsProcessed`

The older form can still work, but removing those actions makes the flow shorter and prevents accidental multi-row processing. Both Update item actions should use the trigger's **ID**, not an ID returned by Get items.

### 4.8 Test the complete integration

Test in this order so failures are easy to isolate:

1. **Flow-only test:** manually create a SharePoint item with a valid email and `IsProcessed = No`. Confirm Teams receives it and the row becomes `Sent`/`Yes`.
2. **Bill assignment:** save Step 3 with a registered user. Within roughly 15 seconds, a `BillAssigned` row should appear and Teams should notify that user.
3. **Payment request:** sign in as that member, upload proof, and select **I've paid**. The transaction uploader should receive the approval notification.
4. **Rejection:** reject the request as its uploader or Admin and enter a reason. The member should receive the reason in Teams.
5. **Pickup winner:** enable Food Pickup Rotation, save a split with at least two eligible registered participants, and confirm the selected account receives the playful winner message and My Bills detail link.
6. Repeat one action with the web language set to **ID** and another with **EN**. Notification language and currency formatting follow the culture active when the event is created.

Expected event routing:

| SplitBill event | Teams recipient | Message contains |
| --- | --- | --- |
| Bill assigned | Registered participant | Merchant, amount, uploader, clickable My Bills detail link |
| I've paid submitted | Transaction uploader | Member, merchant, transaction number, amount, clickable Approvals link |
| Payment rejected | Registered participant | Merchant, transaction number, amount, rejecting actor, reason, clickable My Bills detail link |
| Pickup person selected | Selected registered participant | Playful winner message, merchant, transaction, participant count, selection strategy/probability when applicable, clickable My Bills detail link |

Guests remain valid split participants but do not receive Teams notifications. SharePoint or Teams outages do not roll back business actions: SplitBill saves a local outbox row, attempts delivery every 15 seconds, and retries failures with bounded exponential backoff up to eight attempts.

Only notifications created after the deep-link release contain action links. Existing SharePoint rows and sent messages are not rewritten.

### 4.9 Diagnose notification delivery

| Symptom | Likely check |
| --- | --- |
| Connection test works, but no row appears | Integration is enabled and saved; registered recipient has an email; selected-site role is `write`, not `read` |
| Row appears, but flow never starts | Flow is turned on; trigger points to the exact site/list; SharePoint connection is healthy |
| Flow fails at Teams | Recipient email exists in the tenant and is reachable by the Flow bot connection; inspect run history |
| `IsProcessed` stays `No` | Teams action failed/timed out or success branch Run After/ID mapping is wrong |
| `IsProcessed` becomes `Yes`, but no message is visible | Verify recipient mapping and the signed-in Teams connection; inspect the Teams action output |
| `<br>` appears literally | Put `Description` directly in the Teams rich message field; do not wrap it as code or HTML-encode it again |
| Update item starts the flow again | Replace the trigger with **When an item is created** |
| Duplicate Teams message | Check Flow run history for manual resubmission/retry; one SplitBill event creates one SharePoint row, but connector delivery is effectively at-least-once |

Microsoft references: [create a SharePoint list](https://learn.microsoft.com/en-us/sharepoint/dev/business-apps/get-started/set-up-sharepoint-site-lists-libraries), [create a list item with Microsoft Graph](https://learn.microsoft.com/en-us/graph/api/listitem-create?view=graph-rest-1.0), [selected SharePoint permissions](https://learn.microsoft.com/en-us/graph/permissions-selected-overview), and [Power Automate designer Run After/retry settings](https://learn.microsoft.com/en-us/power-automate/flows-designer).

## 5. Troubleshooting

| Result | Check |
| --- | --- |
| `AADSTS7000215` | Client secret was rejected; use the Secret Value from the same App Registration, not the Secret ID |
| `AADSTS7000222` | Client secret expired; create a replacement and copy its Secret Value |
| `AADSTS700016` | Application ID was not found in that tenant; recheck Tenant ID and Application (client) ID |
| `AADSTS7000112` | App Registration is disabled in Microsoft Entra |
| `AADSTS900023` | Tenant ID is invalid or is not recognized by Microsoft Entra |
| `GRAPH_HTTP_401` | Entra issued a token, but Graph rejected it; verify Microsoft Graph **Application** permissions and admin consent |
| `GRAPH_HTTP_{status}_{code}` | Graph was reached but returned an otherwise-unmapped HTTP/provider error; use the displayed status and safe provider code for diagnosis |
| `ENTRA_HTTP_...` | The token endpoint rejected the request without an AADSTS envelope; inspect service-principal sign-in logs using the HTTP code |
| Entra authentication failed without a code | Recheck the credentials and inspect Microsoft Entra service-principal sign-in logs |
| App has no access to site | Admin consent for `Sites.Selected` and the explicit selected-site grant |
| Site not found | Site URL is the site itself, with the correct tenant hostname and `/sites/...` path |
| No lists found | The app can reach the site but there are no visible selectable lists; check hidden/system list status |
| Microsoft Graph rate limited | Wait and retry; do not repeatedly click Test Connection |
| Network failure | Confirm the IIS server can reach `login.microsoftonline.com` and `graph.microsoft.com` over outbound HTTPS |

SplitBill safely extracts the numeric `AADSTS` code from failed token responses and shows a localized action for common credential failures. Unmapped Graph responses expose their HTTP status, sanitized provider error code, and only the top-level single-line provider message capped at 300 characters. Inner-error payloads are discarded. Diagnostic logs retain safe codes, HTTP status, and request ID where available; complete Entra/Graph response bodies, secrets, and tokens are never logged or rendered. If a secret is suspected to be exposed, revoke it in Entra, create a replacement, test the new value, and remove the old secret according to the organization's policy.

List discovery requests the Graph `list` facet and reads visibility from `list.hidden`; `system` remains a top-level list facet. This matches the Microsoft Graph list contract and keeps hidden/system-managed lists out of the selector.

## 6. Deployment notes

The configuration is stored in the SQLite `SharePointConfigurations` table and the secret depends on `App_Data/data-protection-keys`. During an IIS update, back up and preserve `publish/App_Data` and the key ring. Do not copy a development database or development key ring to another server. The new server creates its schema on first startup; an Admin enters its own Entra secret through the page.

Teams messages include clickable SplitBill deep links. Bill assignments, rejections, and pickup-winner events open the relevant Member bill detail; payment requests open the uploader's Approvals page. The origin is captured from the Admin/Moderator request that saves the split, so uploaders must use an address reachable by recipients. A raw Tailscale IP works only for devices connected to the tailnet; use a trusted HTTPS hostname when browser push is also required.
