# SplitBill Design System

This document is the canonical visual and interaction reference for SplitBill. It captures the design already implemented by the Razor views and `wwwroot/css/site.css` so future work can extend the product without changing its character.

The running application remains the final source of truth. When this guide and the implementation differ, inspect the relevant Razor view, CSS, JavaScript, and localized resources, then update this document with the intentional result.

## 1. Design intent

SplitBill should feel clear, friendly, and trustworthy while handling details that can easily become confusing: receipt evidence, item ownership, discounts, fees, payment states, currencies, and pickup turns.

The interface follows five principles:

1. **Show the math.** Display the receipt value, the participant share, every allocated adjustment, and the reconciled total where the user needs to make a decision.
2. **Make the next action obvious.** Lime identifies the selected state or primary action. A page should not present several actions with equal visual weight.
3. **Keep evidence close.** Receipt photos and payment proofs open in the shared in-page viewer instead of taking the user to a new browser tab.
4. **Use progressive detail.** Lists stay scannable; details, allocation groups, approval evidence, and share controls expand only when needed.
5. **Work at phone width first.** Every workflow must remain usable with touch, long localized labels, large amounts, and many participants.

## Microsoft Integration, login, and admin settings

- Keep the existing local login as the visually primary fallback; place **Sign in with Microsoft** as a clearly labeled secondary action and never imply that Microsoft login creates a new account.
- The Admin Microsoft Integration page uses one credential section and separate **Sign-in / SSO** and **SharePoint** tabs. Show status, revision, migration result, and safe error text, but never render a client secret or token.
- Make independent switches explicit. Explain that disabling SSO does not disable SharePoint, and disabling shared SharePoint credentials does not erase the legacy connection.
- Account linking must identify the currently authenticated local account and the verified Microsoft display identity before confirmation. Long names/emails wrap rather than overflow; cancellation and stale-state errors stay visible and do not discard the local session.
- The Microsoft button, tabs, forms, and validation summaries use existing focus states, keyboard order, mobile spacing, and ID/EN localization. Test at desktop and phone widths with long localized labels.
### 2.1 Core palette
 | Value | Purpose |
| --- | --- | --- |
| Lime / `--primary` | `#CDFF70` · `rgb(205, 255, 112)` | Primary action, active navigation, selected control, progress, avatar and icon background |
| Emerald / `--emerald`, `--ink`, `--primary-dark` | `#003A40` · `rgb(0, 58, 64)` | Main text, sidebar, strong panels, primary hover, high-emphasis totals |
| Stone Grey / `--muted` | `#444547` · `rgb(68, 69, 71)` | Secondary text, supporting labels, inactive controls |
| Cool White / `--bg` | `#F2F0FA` · `rgb(242, 240, 250)` | Application canvas, light text on Emerald, subtle row fills |
| Surface / `--surface` | `#FFFFFF` | Cards, forms, tables, modal panels, mobile chrome |

The canonical CSS tokens are:

```css
:root {
  --primary: #CDFF70;
  --primary-dark: #003A40;
  --primary-soft: rgba(205, 255, 112, .28);
  --emerald: #003A40;
  --ink: #003A40;
  --muted: #444547;
  --line: rgba(68, 69, 71, .16);
  --surface: #fff;
  --bg: #F2F0FA;
  --success: #003A40;
  --warning: #8a6500;
  --danger: #b52c42;
  --shadow: 0 12px 36px rgba(0, 58, 64, .09);
  --radius: 18px;
}
```

Lime is an accent, not a page background. Emerald carries large dark surfaces and high-emphasis text. Cool White separates the application canvas from white cards.

### 2.2 Semantic colors

| Meaning | Background | Foreground | Common use |
| --- | --- | --- | --- |
| Paid / success | `#DCF8EC` | `#087D50` | Paid badge, healthy connection, success confidence |
| Partial / warning | `#FFF3D2` | `#A86C00` | Partial payment, waiting state, warning confidence |
| Unpaid / danger | `#FFE8EC` | `#BD3047` | Unpaid badge, subtraction emphasis, rejected state |
| Draft / neutral | `#EEF0F4` | `#687086` | Draft badge, inactive metadata |
| Information | `#E1F4FB` | `#176C88` | Reopened payment and informational state |

Notification rows add a narrow color rail and matching icon tile:

- Bill assigned: violet.
- Payment submitted: amber.
- Payment approved: green.
- Payment rejected: red.
- Payment reopened: blue.

These colors support the text and icon. Never communicate a state through color alone.

### 2.3 Typography

The application uses the following system-first stack and does not require a remote font download:

```css
font-family: Inter, "Segoe UI", system-ui, -apple-system, sans-serif;
```

The HTML root is `15px`. Use the existing hierarchy:

| Style | Size | Weight | Notes |
| --- | --- | --- | --- |
| Page title | `2rem` desktop, `1.65rem` mobile | `800` | Tight `-.035em` tracking; one per page |
| Card title | about `1.08rem` | `800` | Paired with a short muted description |
| Main body | `1rem` | `400–600` | Emerald for important content, Stone Grey for support |
| Label | about `.79rem` | `750` | Above the related input |
| Eyebrow | `.72rem` | `800` | Uppercase appearance through wide `.14em` tracking |
| Metadata | `.72–.88rem` | `400–700` | Dates, counts, hints, transaction numbers |
| Numeric highlight | `1.65–1.85rem` | `800–900` | Totals and dashboard values |

Amounts use tabular alignment where a component compares rows. Keep currency code or symbol beside the amount and use the current UI culture for separators.

### 2.4 Shape, spacing, and elevation

The interface uses a soft geometric language:

- Page/card radius: `18px`.
- Subcard and panel radius: `14–17px`.
- Button and input radius: `10–11px`.
- Compact control radius: `8–9px`.
- Avatar radius: `12px`.
- Status and filter chips: pill radius `999px`.
- Default card shadow: `0 12px 36px rgba(0, 58, 64, .09)`.
- Borders: one pixel with `--line`; use a stronger Emerald or Lime border for focus and selection.

Use the existing spacing rhythm rather than arbitrary values: `6`, `8`, `10`, `12`, `14`, `16`, `18`, `20`, `24`, `28`, and `38px`. Card padding is normally `24px` desktop and `18px` mobile. Gaps between major cards are usually `18px`.

## 3. Application shell

### 3.1 Desktop

- A fixed `244px` Emerald sidebar constrained to viewport height (`100vh`/`100dvh`) with `overflow: hidden` contains the brand, ID/EN switcher, role-scoped navigation, account controls, and version.
- The brand, ID/EN language switcher, account footer, and version block are non-shrinking (`flex-shrink: 0`), keeping identity and session controls continuously visible.
- The navigation region (`.side-nav`) is an independent scroll container (`flex: 1 1 0; min-height: 0; overflow-y: auto`) with a subtle thin Lime scrollbar (`rgba(205, 255, 112, .35)`), ensuring all menu links are reachable without zooming or pushing profile controls below the viewport.
- At exceptionally short desktop viewports (`@media (min-width: 761px) and (max-height: 520px)`), an explicit fallback enables full-sidebar scrolling (`.sidebar { overflow-y: auto }`, `.side-nav { overflow-y: visible }`) so every control remains reachable without clipping.
- The main region begins after the sidebar with `38px clamp(24px, 4vw, 64px) 70px` padding and a `1640px` maximum width.
- Active navigation uses a Lime rounded rectangle with Emerald text. When Notifications is active, the unread counter badge inverts to Emerald background with Lime text.
- Navigation labels are short; an icon or symbol appears in a fixed-width leading slot (`.nav-icon`). Notifications uses a consistent outline bell SVG.
- The account block (`.sidebar-user`) stays pinned above the version with a subtle white border. Username and role text wrap safely inside `.sidebar-user-info` (`minmax(0, 1fr)`) without horizontal overflow.
- Desktop logout uses an outline door-and-outward-arrow SVG button (`.sidebar-logout`) with a minimum 44×44px hit target, localized `aria-label` and `title`, and clear Lime hover/focus outlines. The application version sits below it in quiet uppercase metadata.

### 3.2 Mobile

At `760px` and below:

- Hide the desktop sidebar.
- Show a sticky white header with brand, language switcher, notification link, profile avatar, and logout access.
- The mobile notification link uses the outline bell SVG with a 44×44px hit target and clear badge clearance; unread badges (e.g. 1, 15, 50, 110/99+) display without clipping or overlapping adjacent chrome.
- Mobile logout uses a visible localized text button (`Keluar` / `Logout`) paired with the shared door-and-outward-arrow SVG, meeting a 44px minimum touch target.
- At very narrow phone widths (`<=380px`), header controls tighten gracefully to guarantee all items remain accessible without crowding.
- Show a fixed, horizontally scrollable bottom navigation. Each destination occupies a stable `76px` slot.
- Keep the active destination Lime or Lime-soft; Upload is not allowed to leave Transactions highlighted.
- Remove the desktop left margin and use `24px 14px 94px` main padding so content clears the bottom bar.
- Stack multi-column page layouts and let wide data tables scroll inside their own container.

Additional responsive thresholds are component-specific:

- `1050px`: dashboard and high-density grids reduce columns.
- `900px`: wide sharing/header arrangements collapse.
- `600px`, `420px`, and `360px`: dense controls, breakdown columns, and very small phone layouts tighten further.

Do not create a new global breakpoint unless the existing thresholds cannot express the layout.

### 3.3 Role-aware navigation

| Destination | Admin | Moderator | Member |
| --- | :---: | :---: | :---: |
| Dashboard | Yes | Yes | Yes, personal dashboard |
| Upload receipt | Yes | Yes | No |
| Transactions | Yes | Yes, owned only | No |
| Report | Yes | Yes, owned only | Yes, member scope |
| My bills | Yes | Yes | Yes |
| Approvals | Yes | Yes, owned only | No |
| Notifications | Yes | Yes | Yes |
| AI, currency, SharePoint, users, pickup, Update Center | Yes | No | No |

Hiding a destination is presentation only. Every request still requires server-side authorization and transaction scoping.

## 4. Page composition

### 4.1 Page heading

Every application page begins with `.page-heading`:

1. Optional eyebrow or back link.
2. One `h1`.
3. One short sentence or transaction metadata line.
4. An optional right-side status or primary action.

Use `.page-heading.compact` for detail and workflow pages. On narrow screens, actions move below the title instead of compressing it.

### 4.2 Cards and sections

`.content-card` is the standard container: white surface, subtle border, `18px` radius, shadow, and `24px` padding. Avoid wrapping every small line in a card; cards should represent a meaningful group.

Inside a card, `.section-head` aligns a title/description pair with an optional action. Place summaries above detailed data and keep destructive controls visually separate from primary flow controls.

### 4.3 Dashboard hierarchy

Admin and Moderator dashboards use this order:

1. Greeting and Upload shortcut.
2. Four operational summary cards.
3. Monthly bill-value visualization and oldest outstanding highlight.
4. Outstanding people, top merchants, and settlement-time cards.
5. Transaction-scoped currency trend when available.
6. Recent transactions.
7. Current currency-rate cards and their inline selection editor at the bottom.

Members receive a private spending dashboard with their current obligation, paid/outstanding summaries, monthly history, and recent bills. Do not expose organization-wide operational statistics in the Member view.

### 4.4 Dense data

- Use `.app-table` for desktop-first lists and wrap it in `.table-responsive`.
- Table headers are small uppercase labels; rows use a strong primary value and a muted secondary line.
- Keep row actions in the last column.
- On dedicated mobile designs, use stacked cards when horizontal scrolling would hide the relationship between identity, status, and action.
- Empty data uses `.empty-inline` or `.empty-state` with an icon, title, and one explanatory sentence.

## 5. Core components

### 5.1 Buttons

| Variant | Use |
| --- | --- |
| `.btn-primary` | One main action: Process with AI, Save, Apply, Confirm, I've paid |
| `.btn-soft` | Secondary action on a normal surface or a subdued action on a dark panel |
| `.btn-light` | Back, cancel, filter toggle, neutral utility |
| `.btn-outline-danger` | Delete, revoke, reject, or another destructive action |
| `.btn-sm` | Row-level utilities only |

Primary buttons are Lime with Emerald text. Hover/focus inverts to Emerald with Lime text. New icon-only controls need an accessible name, a visible tooltip/title where useful, and a minimum `42–44px` hit area.

### 5.2 Forms

- Put labels above fields and hints directly below them.
- Use `.form-grid.two` for related pairs; it becomes one column on mobile.
- Inputs use a `10px` radius and a Lime focus ring around an Emerald border.
- Use radios or selectable cards for mutually exclusive modes.
- Use account cards instead of a registered-user dropdown.
- Show all real server errors in `.validation-summary`; hide valid or empty summaries.
- Put final actions in `.form-actions`, aligned right on desktop and full-width when a mobile flow benefits from it.
- Decimal fields still post invariant values. Quantities remain positive whole numbers.

### 5.3 Status badges

Use `.status` with one semantic modifier: `.draft`, `.unpaid`, `.partial`, or `.paid`. `AwaitingConfirmation` uses the warning/partial treatment. Status text must remain present even when an icon is shown.

### 5.4 Identity and counters

- Avatars show the first uppercase character in an Emerald-on-Lime rounded square.
- Notification counts use a pill with a minimum width, so `9`, `15`, `50`, and `110` remain centered without becoming circular or clipped.
- Participant and role chips use Lime-soft backgrounds; a selected pickup person adds a visible `★ pickup person` label and a highlighted container.

### 5.5 Feedback

- Success and error messages appear near the top of the main region through `.app-alert`.
- Connection tests use textual state in addition to color.
- Long-running AI processing uses a full-screen Emerald translucent overlay, centered progress card, animated orbit, and progress line.
- PWA install/update hints use a Lime-soft banner with a clear action.
- Disable or replace the initiating action while a request is running to avoid duplicate submissions.

### 5.6 Dialogs and media viewer

Use a dialog or the shared receipt modal for focused tasks. Opening a dialog locks background scrolling; closing it restores the previous scroll state and focus.

All clickable receipt and payment-proof images use the shared modal:

- Emerald translucent backdrop.
- White panel, maximum width `920px`, constrained to the viewport.
- Previous/next controls for a gallery.
- Zoom controls with the current zoom label.
- Close by button, backdrop, or `Escape`.

### 5.7 Segmented tab controls

For multi-view configuration surfaces such as the unified Microsoft Integration page (`/admin/microsoft`):

- **Container (`.integration-tabs-nav`):** Light neutral grey track (`background: #e9ecf2; border: 1px solid rgba(68, 69, 71, 0.12); border-radius: 12px; padding: 4px; gap: 4px;`).
- **Inactive tab (`.tab-btn`):** Transparent background, muted text (`color: var(--muted)` `#444547`), neutral circular bullet (`#a0a6b5`), smooth hover transition (`background: rgba(255, 255, 255, 0.6)`).
- **Active tab (`.tab-btn.active`):** SplitBill signature Lime pill (`background: var(--primary)` `#CDFF70`), high-contrast deep emerald text (`color: var(--emerald)` `#003A40`, `font-weight: 800`), dark emerald bullet dot, and subtle elevation shadow (`0 4px 12px rgba(0, 58, 64, 0.14)`).
- Meaningful `alt` text and accessible button labels.

Do not open protected images in a new browser tab as the primary interaction.

## 6. Product screen blueprints

### 6.1 Authentication and first setup

Desktop uses a two-panel composition: an Emerald product statement on the left and a focused white login/setup card on the right. On mobile the showcase hides and the form becomes the full experience. The language switcher stays visible before authentication.

The form contains only the fields required for the current action. Password visibility is a local convenience; errors remain attached to the form.

### 6.2 Receipt wizard

The receipt flow keeps a visible **Step 1 of 3**, **Step 2 of 3**, or **Step 3 of 3** eyebrow.

**Upload receipt**

- Large dashed drop zone as the dominant control.
- Upload icon, direct instruction, file limits, and supported-format hint.
- Ordered thumbnail gallery after selection.
- Tips card beside the drop zone on desktop.
- Cancel and **Process with AI** actions after the content.

**Review AI result**

- Receipt gallery beside editable merchant, date, currency, items, and adjustment rows.
- Each item has a visible Row/Baris caption.
- Quantity is an integer; price and adjustment amounts may be decimal.
- Dynamic adjustments show their printed label, nominal value, operation, and calculated percentage hint.
- AI confidence is context, never an approval decision.

**Set split**

- Participant selection sits in the left column on desktop and first on mobile.
- Registered accounts use selectable cards; guests use a simple name field and chip.
- Equal and By item modes are large radio cards.
- Each receipt item shows its row number, value, assigned quantity, remaining quantity, and progress bar.
- Allocation groups contain a whole quantity and one or more people. One person means ownership; several people means equal sharing of that group.
- Add group and remove group remain available until all quantities reconcile.
- Optional food pickup shows a preview of eligible participants without exposing the final random winner before save.

### 6.3 Transaction details

The detail page follows this visual hierarchy:

1. Back link, merchant, transaction metadata, status, and unified Share icon.
2. Full-width summary header. The left side shows a clickable receipt thumbnail and total; the right side shows the pickup person on an Emerald panel. Action buttons occupy their own footer row and never cover the total.
3. Payment status card with one participant block per person. The pickup person receives a Lime highlight and badge.
4. Each participant block has a native **View order details** expansion with items, allocated adjustments, percentages, subtotal, and final amount.
5. Receipt details stay in a separate comparison card.
6. All receipt photos use the shared viewer.

The unified Share dialog contains Link and JPG tabs. The Link tab manages the whole-transaction token and guest-specific tokens. The JPG tab is available to the uploader or Admin for Draft, Unpaid, Partial, or Paid transactions, including guest-only transactions.

### 6.4 My Bills details

My Bills is the transparency-first participant view:

- A dark total strip states the final obligation and its item/adjustment equation.
- A reference-currency strip appears only when the receipt currency differs from reporting currency.
- The pickup winner receives a prominent Emerald congratulations banner.
- The main breakdown compares **Description**, **Full receipt**, and **Your share**.
- Items, menu subtotal, every discount/fee with effective percentage, adjustment total, and bill total remain visible in that order.
- Receipt photos and payment controls live in the right column on desktop and below the breakdown on mobile.
- Payment attempts preserve status, time, rejection reason, and proof access.

The My Bills list stays compact: show at most two item summaries, the final participant amount, state, and Details action. The full math belongs on the Details page.

### 6.5 Reports

- Date, status, and Admin uploader filters appear before totals.
- Reset clears every filter field, including dates.
- Summary cards include all scoped payment states.
- The detailed report exposes merchant, uploader where permitted, totals, outstanding value, pickup person, and status.
- Food Pickup Rotation is a separate card at the bottom, never above the financial report.
- Excel export uses the active filter and preserves Summary, Payment Details, Pivot by Person, and Pickup Rotation sheets.

### 6.6 Notifications and approvals

Notifications are chronological rows with a type-specific rail/icon, unread background, merchant/transaction context, time, and the closest next action. Rejection includes the reason inline.

Approvals use a responsive table on desktop. Payment proof opens in the shared modal. Confirm is primary; Reject requires a visible reason field and destructive styling.

### 6.7 Admin settings

Admin pages use the same heading, content card, form grid, connection state, and action conventions. Settings pages should not invent separate dashboards.

- **AI settings:** provider choice, protected key entry, endpoint/model discovery, and connection test.
- **Currency settings:** default currency, provider URL/authentication, encrypted optional key, refresh, and health state.
- **SharePoint integration:** Entra credentials, site URL, test result, populated list choice, and save.
- **Manage users:** filterable table/cards, role pills, create/edit/reset actions, and audit-safe text.
- **Food pickup rotation:** strategy, eligibility cards, history, and transparent counts without revealing a future random winner.
- **Update Center:** application version card first, then backup and restore tools, followed by the destructive-operation warning.

### 6.8 Guest pages

Guest pages are read-only and account-free. They use the normal card language without authenticated navigation. A compact tab switcher moves between **My bill** and **Whole transaction** when the token permits both.

Never expose account IDs, email addresses, usernames, payment proofs, admin actions, or internal file paths. Receipt images remain token-scoped and open in the same modal viewer.

## 7. Financial and currency presentation

- Financial values rendered in Razor must come from calculator-owned server projections. JavaScript may format already-projected display values for a share image, but it must not calculate obligations.
- A participant row shows the amount owed after discounts and fees. Item rows show their item-share amount, not the final bill amount.
- Discounts use a minus sign and danger color. Added fees use a plus sign and Emerald.
- Effective percentages are supporting explanations calculated from nominal values; the saved nominal amount remains authoritative.
- Receipt currency is the payment obligation. Reporting currency is a labeled reference value.
- When currencies differ, show rate, effective date, and source close to the converted value.
- Dashboard rate charts use server-projected points and up to seven stored effective dates.

## 8. Share JPEG design

The export is a single `1000px`-wide JPEG with dynamic height. It must grow vertically for every included participant; never crop at a viewport or screenshot boundary.

The image order is:

1. SplitBill mark and transaction identity.
2. Receipt total, status, currency context, and pickup person.
3. One complete participant block at a time.
4. Item shares, discounts/fees, adjustment total, and participant total.
5. Receipt reconciliation summary with enough spacing before its subtotal.

`Shared with` appears only for a fractional share below one whole item. A whole quantity such as `×1` is ownership and does not receive that label. Export strings are projected by the server and localized before Canvas rendering.

## 9. Localization and content

SplitBill supports `id-ID` and `en-US` everywhere.

- Every user-facing Razor string uses `IStringLocalizer<SharedResource>` through `L["Key"]`.
- Both resource files keep identical key sets.
- The language control is a two-segment `ID | EN` pill with `aria-pressed` state.
- Allow headings, buttons, table headers, and bottom navigation labels to grow or wrap without clipping.
- Use direct, friendly language. State what happened, what the user owes, or what action is next.
- Error messages explain the correction. Do not show raw exceptions, provider payloads, tokens, or secrets.
- Humorous pickup copy is acceptable when it remains clear and localized; payment and security messages stay literal.

## 10. Accessibility requirements

- Maintain logical heading order and one page-level `h1`.
- Associate every form field with a label.
- Give icon-only buttons an `aria-label` and hide decorative SVG/emoji from assistive technology.
- Use `aria-live="polite"` for copy/share/test feedback and other asynchronous status text.
- Preserve visible keyboard focus. Selected cards also expose `aria-pressed`, checked inputs, or semantic state.
- Dialogs identify their title, close with `Escape`, and return focus to the trigger.
- Images use meaningful alternate text; decorative thumbnails are not the only way to identify an action.
- Keep text and state labels present alongside semantic color.
- Design new touch controls around a `44px` target even when their visible icon is smaller.
- Respect the user's reduced-motion preference when adding new animation. Existing motion is brief and task-related.

## 11. Implementation rules

When adding or changing UI:

1. Reuse `page-heading`, `content-card`, `section-head`, button, form, status, table, empty-state, and modal patterns before creating a new component.
2. Put reusable styles in `wwwroot/css/site.css`; avoid page-level inline styles.
3. Use Bootstrap utilities only for small layout adjustments. The application CSS owns the product identity.
4. Keep behavior in focused vanilla JavaScript files and enhance semantic HTML instead of replacing it.
5. Render financial calculations and authorization decisions on the server.
6. Test Admin, Moderator, Member, and guest scopes where the screen differs by role.
7. Verify both languages at desktop and phone width.
8. Verify empty, loading, success, warning, error, disabled, and long-content states.
9. Keep protected images outside `wwwroot` and load them only through authorized or token-scoped endpoints.
10. Update this document and the sanitized gallery when a new pattern changes the product's visual language.

## 12. UI review checklist

Before merging a user-interface change, confirm:

- [ ] One obvious primary action exists.
- [ ] Page heading and card hierarchy match existing screens.
- [ ] Lime, Emerald, Stone Grey, and Cool White retain their established roles.
- [ ] Amounts reconcile and percentages are explanatory.
- [ ] Status has readable text in addition to color.
- [ ] Long names, amounts, transaction IDs, and translated labels do not overlap.
- [ ] Tables or dense controls remain usable at `760px`, `420px`, and `360px`.
- [ ] Bottom navigation and the sticky mobile header do not cover content.
- [ ] Keyboard focus, labels, live feedback, and modal close behavior work.
- [ ] Receipt and proof images open inside the shared viewer.
- [ ] Empty and failure states explain the next step.
- [ ] Admin-only or ownership-scoped actions are also enforced by the server.

## 13. Source map and visual references

| Concern | Source |
| --- | --- |
| Global shell and role navigation | `Views/Shared/_Layout.cshtml` |
| Design tokens and components | `wwwroot/css/site.css` |
| Shared receipt/proof viewer | `Views/Shared/_ReceiptModal.cshtml`, `wwwroot/js/site.js` |
| Dashboard patterns | `Views/Dashboard/Index.cshtml` |
| Receipt wizard | `Views/Transactions/Upload.cshtml`, `Review.cshtml`, `Split.cshtml` |
| Operational transaction view | `Views/Transactions/Details.cshtml` |
| Participant transparency view | `Views/MyBills/Details.cshtml` |
| Report hierarchy | `Views/Reports/Index.cshtml`, `Details.cshtml` |
| Guest read-only views | `Views/Guest/MyBill.cshtml`, `Transaction.cshtml` |
| Share JPEG renderer | `wwwroot/js/transaction-share.js` |
| Localization | `Resources/SharedResource.resx`, `SharedResource.en-US.resx` |
| Sanitized screen gallery | `documentation/screenshots/` |
| Gallery source | `documentation/screenshots/source/readme-gallery.html` |

Current sanitized references:

- `documentation/screenshots/dashboard.png`
- `documentation/screenshots/upload-receipt.png`
- `documentation/screenshots/split-assignment.png`
- `documentation/screenshots/transaction-pickup.png`
- `documentation/screenshots/my-bill-details.png`
- `documentation/screenshots/report-pickup.png`
- `documentation/screenshots/share-transaction-long.jpg`

### Visual gallery

| Operational dashboard | Receipt upload |
| --- | --- |
| [![Admin dashboard](documentation/screenshots/dashboard.png)](documentation/screenshots/dashboard.png) | [![Receipt upload](documentation/screenshots/upload-receipt.png)](documentation/screenshots/upload-receipt.png) |

| Quantity-based split | Transaction details |
| --- | --- |
| [![Set split](documentation/screenshots/split-assignment.png)](documentation/screenshots/split-assignment.png) | [![Transaction with pickup person](documentation/screenshots/transaction-pickup.png)](documentation/screenshots/transaction-pickup.png) |

| Member transparency | Reports and pickup history |
| --- | --- |
| [![My Bills details](documentation/screenshots/my-bill-details.png)](documentation/screenshots/my-bill-details.png) | [![Report](documentation/screenshots/report-pickup.png)](documentation/screenshots/report-pickup.png) |

The long export reference is [one complete production-rendered JPEG with ten participants](documentation/screenshots/share-transaction-long.jpg).

### Account settings and explicit Microsoft linking
The desktop profile footer now uses a gear plus Account settings, with the same entry in the mobile header. Keep logout separate. Settings show a local-password card and a Microsoft card with disconnected/connected state, email, linked date and connect/disconnect controls. Username admin sees only the password card. Password confirmation and returned-identity confirmation use focused dialogs; the latter compares local and Microsoft identities before saving. Passwordless Microsoft registrations explain the Admin password-reset requirement before unlinking. The Admin SSO tab adds a default-off automatic Member registration checkbox.
