# BusGo

BusGo is an ASP.NET Core Razor Pages intercity ticket-sales and station-operations web app for Vietnam, migrated from the BusTicketSaleSystem WPF desktop client. It keeps the original SQL Server database and business rules, including atomic seat allocation, account lockout, payment settlement, ticket cancellation, station-scoped administration, commissions and scheduled payment expiry. It does not alter the existing desktop project.

## Run locally

Requirements: Windows or Linux, .NET 10 SDK, SQL Server reachable from the application host, and a database login able to create/update the configured catalog.

From this directory:

```powershell
dotnet restore BusGo.csproj
dotnet build BusGo.csproj
dotnet run --project BusGo.csproj
```

The development launch profile opens `http://localhost:5080`. VS Code: select **BusGo web** in Run and Debug. First launch applies the source app's checksum-verified SQL migrations to `BusTicketSaleSystem`; `Database:SeedUpcomingTrips` retains the desktop's existing safe schedule top-up. Configure the connection before starting if you do not intend to use the existing local development database. The app never drops or resets the database.

## Database and secrets

`appsettings.json` provides local SQL Server defaults, not credentials. Use environment variables for machine-specific or sensitive configuration:

| Variable | Purpose |
| --- | --- |
| `BUS_TICKET_CONNECTION_STRING` | Application database connection string. Defaults to local integrated-auth `BusTicketSaleSystem`. |
| `BUS_TICKET_MASTER_CONNECTION_STRING` | `master` connection used only if the configured catalog does not yet exist. |
| `STATION_LOCATION` | Fixed station for administrator operations (for example `Thái Nguyên`). |
| `BREVO_API_KEY`, `EMAIL_SENDER_ADDRESS` | Optional transactional ticket email configuration. |
| `EMAIL_SENDER_NAME` | Optional sender display name. |
| `VNPAY_TMN_CODE`, `VNPAY_HASH_SECRET` | Optional original VNPay credentials. VNPay remains disabled in the product until merchant credentials and a publicly reachable HTTPS callback are configured. |
| `VNPAY_RETURN_URL` | Public HTTPS application callback URL if VNPay is enabled. |

Do not put secrets in tracked configuration. For local development use environment variables, user secrets, or ignored `appsettings.Development.json`. Cookie sessions use HttpOnly and SameSite=Lax cookies, refresh the account/role from the database on each request, and expire after eight hours. Deploy behind HTTPS; production enforces HTTPS redirection and HSTS.

The schema scripts under `Data/` are byte-for-byte copies of the desktop migrations, preserving their immutable SHA-256 checksums. Add new SQL migrations using the original instructions in the desktop repository; never edit an applied script. Existing customer, payment and station data remains in the legacy-named database.

## Screens

- Customer: Vietnamese/English trip search with accent-insensitive city autocomplete, today/tomorrow date shortcuts, company/vehicle/departure-period/seat-availability/sort filters and paged ticket-card results; operator directory and profile pages; sourced route-reference guides; FAQ help with payment and cancellation anchors; multi-seat keyboard-operable booking with progress steps, passenger/payment review and booking confirmation; boarding-pass ticket detail with status-dependent presentation, PDF/PNG/QR ticket, email/map links, cancellation and refund request; journey list with filter tabs and travel history with distance summary.
- Account: split-panel registration and sign-in with landscape background, profile with personal information and password sections, sign-out, and application language/appearance settings available to signed-in users and guests.
- Station admin: desktop sidebar with separate management and counter-sales groups, compact account/station header and collapsible tablet/mobile navigation. The dashboard separates quick tasks, pending settlements, walk-in ticket sales, upcoming departures, system-wide financial totals and recent bookings; fleet lookup is collapsible. Routes, fleet, schedules, accounts/roles, payment settlement/refunds, check-in and revenue/commission reports share the admin-only `wwwroot/css/admin.css` presentation. Counter sales retain separate passenger details and require cash-receipt acknowledgement before issuing a paid ticket.
- Design: Manrope variable font (self-hosted, Vietnamese subset), forest-green palette, responsive from 390 px to 1440 px with no horizontal overflow. Brief hover/focus transitions suppressed for `prefers-reduced-motion: reduce`; mobile menu toggles with button, closes with Escape or outside click. Customer footer includes structured navigation columns; staff footer stays compact. Both retain the academic/demo disclaimer.
- `/health` provides a shallow process readiness response; successful app startup also requires SQL migrations to complete.

The route-reference guide links to public Vexere and redBus listings for Hà Nội–Hạ Long, Hà Nội–Hải Phòng, Hà Nội–Sa Pa and TP. Hồ Chí Minh–Đà Lạt. These are dated research references only: their fares, timetables, availability, reviews and promotions are not copied into BusGo inventory. Only operator-published trips returned by BusGo search can be booked through BusGo. Reconfirm third-party information at its source.
The global footer carries the academic/demo notice. VeXeRe and redBus links in the route guide are dated source references only; their listings are not imported as BusGo bookable inventory.

The web host uses `/Account/Login` for authentication and role-restricts all `/Admin` pages. Customer handlers must verify current account ownership of ticket/payment resources even when an identifier is supplied in a URL or posted form.

### Access governance and separation of duties

The platform implements a strict three-tier hierarchy: **Owner > Administrator > Customer**.
- **Station Owner**: Head of station governance and human resources. The Owner exclusively holds the server-side `CanGrantAdmin` policy to grant or revoke the Administrator role (requiring password re-authentication), view the immutable `AuditLogs` trail, and oversee fleet, routes, schedules and financial reports. In accordance with the principle of separation of duties, operational desk tasks—including walk-in counter ticket sales, passenger boarding check-in, and manual payment approval/settlement—are excluded from the Owner's interface and rejected at the service layer.
- **Administrator / Station Staff**: Station desk operators who issue walk-in tickets at the counter, perform boarding check-in, settle payments, and manage day-to-day departures. Administrators cannot modify privileged roles, grant admin permissions, or alter Administrator/Owner credentials.

## Language settings

Open `/Settings`, choose **Tiếng Việt** or **English**, then save. Vietnamese is the default. The `BusGo.Language` HttpOnly cookie stores `vi-VN` or `en-US` for one year in the current browser and survives reload, sign-in and sign-out. Only this preference selects the request culture; browser language and URL culture parameters do not override it.

The selection covers customer and administration screens, navigation, browser/server validation, notifications, recognized status/category labels, formatted amounts and dates, ticket PDF/PNG exports and the generated ticket-email content. Place names, operator/passenger information, free-form data, SQL status tokens and posted category identifiers remain unchanged. Email delivery still requires the existing Brevo sender/API configuration.

Maintain paired interface copy with `Services/UiText.cs`, known categorical labels with `Services/DomainText.cs`, and validation messages/field names in `Resources/ValidationMessages.resx` and `Resources/ValidationMessages.en.resx`. Unsupported language submissions are rejected without changing the saved preference.

## Light, dark and system appearance

Open `/Settings` → **Appearance**, select **Light**, **Dark** or **System**, then save. The sun/moon control in the header switches directly between light and dark; return to Settings to follow the system again. **System** is the default and reacts to operating-system appearance changes while the page is open.

The non-sensitive `BusGo.Appearance` cookie stores `light`, `dark` or `system` for one year in the current browser. It survives navigation, reload, sign-in and sign-out and is independent of the language preference. The server validates saved values; unsupported submissions do not change the preference. The synchronous, same-origin `wwwroot/js/appearance.js` resolves system mode before the first paint and refreshes the preference on page restoration/tab focus. The settings form also saves without JavaScript; the header link then opens appearance settings.

`wwwroot/css/theme.css` owns paired semantic colors for backgrounds, elevated surfaces, text, controls and status indicators. Customer/account, booking, membership and administration styles consume these pairs, using CSS `light-dark()` and `color-scheme` in current browsers. Dark mode uses deep forest/charcoal surfaces with mint actions rather than inversion or pure-white text on pure black. Photographs stay unfiltered, QR images keep white backing, and print rendering always returns to a light scheme with a white page; downloadable ticket PDFs/PNGs and emails retain their existing document styling.

Both light and dark modes implement Apple-style Liquid Glass (`backdrop-filter: blur(28px) saturate(200%)`, specular highlight rims and multi-layered ambient depth) across navigation, floating search panels, cards, pills and controls. The user header floats sticky with rounded corners (`border-radius: 22px`) following scroll, and the footer forms a sculpted rounded island (`border-radius: 28px`). Location input boxes (Origin and Destination) on both user search panels and admin station route/fleet forms feature dedicated frosted glass blur capsules (`backdrop-filter: blur(16px)`, rounded corners, inset specular sheen and glowing focus rings), paired with high-diffusion floating suggestion popovers (`blur(30px)`). Switching between modes runs a circular liquid wave transition via `document.startViewTransition()` originating from the user's click or toggle button, smoothly expanding the incoming theme across the viewport with continuous icon rotation and cross-scaling.

Fallbacks: browsers without View Transitions use an integrated multi-property 420ms CSS transition; `prefers-reduced-motion: reduce` executes theme switching instantly with zero animation. `prefers-reduced-transparency: reduce` and high contrast settings drop background blurs in favor of solid surface colors. Printed documents and downloadable ticket assets completely strip blur and glass shadows, resetting to crisp black-on-white. Design references: [Apple Dark Mode](https://developer.apple.com/design/human-interface-guidelines/dark-mode), [Apple Materials](https://developer.apple.com/design/human-interface-guidelines/materials), [Google Material 3 color roles](https://m3.material.io/styles/color/roles), and [Microsoft Fluent 2 design tokens](https://fluent2.microsoft.design/design-tokens), consulted October 2026.

## Membership and tier discounts

Customer membership is calculated from lifetime **net settled ticket spending**. Unpaid reservations and refunded payments do not count; refunds can lower the current tier. Migration `019_MembershipDiscounts` adds the following thresholds without changing historical fares or payments:

| Tier | Minimum qualifying spending |
| --- | ---: |
| Standard / Tiêu chuẩn | 0 VND |
| Bronze / Đồng | 500,000 VND |
| Silver / Bạc | 2,000,000 VND |
| Gold / Vàng | 5,000,000 VND |
| Diamond / Kim cương | 10,000,000 VND |

- Customers: `/Customer/Membership` shows the current tier, next-tier progress and eligible active/upcoming campaigns. `/Customer/Notifications` shows the newest 100 account-owned announcements with individual/all-read actions. Opening a notification does not mark it read.
- Administrators: `/Admin/Discounts` creates global percentage campaigns for one or more reward tiers, with local-time inputs saved as UTC. Creation immediately persists announcements for currently eligible customer accounts. Later eligibility is synchronized when the customer opens their overview, membership or notifications; the unique account/campaign key prevents duplicate announcements.
- Eligibility matches the **exact current tier**, not all lower tiers. Booking applies only an active campaign within `StartsAt <= SQL UTC now < EndsAt`. The highest percentage wins; ties use the lowest campaign ID. Discounts never stack and the reduction is rounded to whole VND, with midpoint rounding away from zero.
- Confirmation checks the reviewed amount and campaign again inside the seat-allocation transaction. A changed fare or offer requires another review, rather than silently charging a different amount. The saved discount and resulting amount remain fixed for an issued reservation even after campaign expiry/deactivation.
- Payments, refunds, spending totals, revenue/commission reports, ticket PDF/PNG and generated email amounts use the saved net fare. Counter-staff reservations do not use the staff account's membership discount. Campaigns can be deactivated but are not edited/deleted, preserving notification and ticket history.
- Membership, campaigns, review details and announcements follow the saved Vietnamese/English language preference. Notifications are in-app; the feature does not send promotional email.


## Nationwide location suggestions

The homepage and trip-search page share an offline catalog in `Data/TravelLocationCatalog.cs`: 169 places comprising all 34 current provincial-level units, 29 familiar former province names, 77 towns/travel destinations and 29 bus terminals. Existing route endpoints are merged first, including operator-specific locations not in the catalog. Distinct database spellings retain their original query values so adding suggestions does not remove existing route searches.

Both pickers support accent-insensitive names, spacing variants and aliases such as Sài Gòn/TP.HCM, Sapa and Buôn Mê Thuột. Empty input exposes the scrollable catalog; Arrow Up/Down, Enter and Escape retain the existing keyboard behavior. Each suggestion shows its type and, where applicable, a geographic hint. Hints may use familiar pre-merger area names; they are not current administrative addresses.

This is a curated nationwide place directory, not an exhaustive registry of every operator's pickup point. Terminals remain separate from their cities. Selecting a terminal searches that name; it does not silently substitute a city or imply an available departure. The catalog never inserts routes, trips, fares, availability or pickup confirmations into SQL. An unserved location correctly produces no matching trips. The legacy admin/station-scope province list remains unchanged.

Reference sources consulted on 2026-10-04:
- [Government list of 34 provincial-level units](https://xaydungchinhsach.chinhphu.vn/chi-tiet-34-don-vi-hanh-chinh-cap-tinh-tu-12-6-2025-119250612141845533.htm).
- Public redBus place/terminal names in its route directory: [page 1](https://www.redbus.vn/ve-xe-khach/tuyen-duong/), [page 2](https://www.redbus.vn/ve-xe-khach/tuyen-duong/2), [page 3](https://www.redbus.vn/ve-xe-khach/tuyen-duong/3), [page 4](https://www.redbus.vn/ve-xe-khach/tuyen-duong/4); destination references for [Điện Biên Phủ](https://www.redbus.vn/ve-xe-khach/thanh-pho/ve-xe-di-dien-bien-phu), [Pleiku](https://www.redbus.vn/en/bus-tickets/cities/pleiku), [Long Xuyên](https://www.redbus.vn/en/bus-tickets/cities/long-xuyen) and [Hà Tiên terminal](https://www.redbus.vn/ve-xe-khach/ben-xe/ha-tien/ben-xe-ha-tien).
- [Vexere's Vĩnh Niệm terminal reference](https://vexere.com/vi-VN/ben-xe-vinh-niem).

These links establish reference names only, not BusGo supply or live operating status. Maintenance is manual; update the catalog and its aliases when reference names change. Runtime suggestions require no external network calls.

## Payment and production boundaries

Cash-on-board and manual bank-transfer/VietQR settlement follow the existing application behavior. The QR image is the source project's static bank-transfer image; it does not automatically reconcile bank transactions. VNPay sandbox code and its original signing configuration remain, but the WPF localhost callback listener cannot serve internet customers. Enable VNPay only when the configured callback URL is accessible via public HTTPS and verified against the provider.

Before production: use HTTPS and a secret manager, verify production SQL permissions/backup/recovery, configure an exact public host, test mail and payment providers, and review the deployment threat model. The development login/seed data is not production account provisioning.
