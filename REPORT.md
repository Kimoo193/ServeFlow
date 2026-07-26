# ServeFlow — Consolidated Review Report

Five independent reviews of the Phase-1 codebase, run 2026-07-26 against commit `b12b720`:
security, architecture, UI/UX + accessibility, usability/operations, and capacity.

This document merges and de-duplicates their findings into **one ordered work programme**.
It is a living document — update the status column as you go.

---

## Status legend

| Mark | Meaning |
|---|---|
| ☐ | **Next** — not started |
| ◐ | **In progress** |
| ☑ | **Done** — landed on `main`, with a test |
| ⊘ | **Won't do** — deliberately declined, reason recorded in the row |

Effort is a rough size, not a promise: **XS** < 1h · **S** < half a day · **M** 1–2 days ·
**L** a slice · **XL** multi-slice.

---

## Verdict up front

The engineering that exists is, in places, genuinely good — RLS is real and load-bearing,
the offline idempotency design is correct in a way most POS codebases get wrong, combo
allocation is exact and well-tested, and the password/login endpoint closes both enumeration
oracles deliberately. Those are noted in the "do not regress" appendix at the bottom, because
the risk with a report this long is that someone "simplifies" the parts that are right.

But the system is **not deployable against real money or a real service** today, for five
independent reasons, any one of which is sufficient:

1. **A single request can zero out a bill.** Negative quantities are accepted on the money path.
2. **A Manager can become Owner in one request**, in a stock install, with no audit record.
3. **No bill can ever be closed**, because nothing can move a line to `Served`.
4. **Every order closed from now on writes an unrepairable money value.**
5. **There is no Dockerfile.** The app cannot be built or deployed as-is.

Item 4 is the only one that gets *worse while you read this*, which is why it is first below.

**On scale:** the load itself is trivial — 800 req/s at 1,000 venues is nothing for .NET 10.
The ceiling is **per-request cost, and that cost grows with accumulated history, not with
concurrency**. A fresh database on a 2 vCPU box handles 50+ venues comfortably; after roughly
**3–5 venue-years of `order_events`** the same box falls over regardless of traffic. *The system
degrades on a calendar, not on a traffic curve* — which is precisely the failure mode that
surprises people in production. Four indexes fix most of it, measured at up to 4,000×.

---

## The ordering logic

The phases are not severity-ordered. They are ordered by **cost of delay**:

- **Phase 0** — every hour of delay destroys data or costs money that cannot be recovered later.
- **Phase 1** — cheap fixes to defects that are catastrophic if exploited. Do before any deploy.
- **Phase 2** — the product does not function without these. This is where the waiter app sits.
- **Phase 3** — structural decisions that get monotonically more expensive with every slice
  shipped on top of the current assumption. Cheap now, a migration and an incident later.
- **Phase 4** — quality, accessibility, and polish. Real work, but nothing here is load-bearing.

Within each phase, items are ordered so earlier ones unblock later ones. Item numbers are
**stable IDs** — when something is done, mark it, don't renumber the rest.

### If you only do ten things

The highest value-per-hour path through the whole document. Roughly a week of work, and it
converts "not deployable" into "deployable to one pilot venue with a known gap list".

| Order | Item | Why this one |
|---|---|---|
| 1 | **0.1 + 0.2** money rounding + currency snapshot | Only finding where delay destroys data |
| 2 | **0.3** reject non-positive quantities | One `if`; closes the shortest fraud path |
| 3 | **1.11** the four indexes | Measured up to 4,000×; moves the tier by an order of magnitude |
| 4 | **1.1** privilege escalation | Manager ⇒ Owner in one request, stock install |
| 5 | **1.2** delete `/dev/token` | Ten minutes; removes a total-compromise vector |
| 6 | **2.1** break the close deadlock | Without it, 100% of bills are unclosable |
| 7 | **2.2 + 2.4** table number on tickets + `item_name` snapshot | Kitchen can route plates; receipts stop printing GUIDs |
| 8 | **1.12 + 2.0** pool config + Dockerfile | Currently un-deployable and un-scalable |
| 9 | **3.1** broadcast after COMMIT | Cheapest before the next realtime feature; gets worse with each one |
| 10 | **1.3** append-only enforcement | Makes the fraud controls real rather than advisory |

Everything else is genuinely worth doing — this is the subset that changes the system's status
rather than improving it.

---

## Phase 0 — Stop the bleeding

**Do these before writing any other code.** All four are small; the first two are irreversible
if delayed.

| # | Item | Evidence | Effort | Status |
|---|---|---|---|---|
| 0.1 | **Round money at close and constrain the columns.** The five order money columns default to `numeric(18,6)` (no `HasColumnType`) while every other money column in the schema is explicitly `numeric(10,2)`. `ComputeBill` never rounds. Every order closed from now on stores a total a later migration cannot unambiguously repair — you will not know whether `10.5588` should be `10.56` or whether a reprint means the customer paid something else. Round the *composed* values (`MidpointRounding.AwayFromZero`, 2dp), not per-component, or subtotal + service + tax will not equal total. | `OrderConfiguration.cs:40-48`, `OrderEndpoints.cs:1347-1378`, contrast `OrderLineConfiguration.cs:28-30` · found independently by **architecture (C3)** and **security (M2)** | S | ☐ |
| 0.2 | **Snapshot the currency onto the order.** `BranchSettings.Currency` is live-referenced. Changing it silently reinterprets every historical total — a direct violation of golden rule 6, in the same blast radius as 0.1 and fixable in the same migration. | `BranchSettings.cs:11` | XS | ☐ |
| 0.3 | **Reject non-positive quantities.** `Quantity = body.Quantity` is written unbounded on both add-line and patch-line. No endpoint guard, no `CHECK` constraint. A user holding `TakeOrder` posts `quantity: -20` on a $15 item; it walks Draft→Fired→Served through the normal endpoints (none inspect quantity), passes the close check, and snapshots a subtotal $300 lower with service and tax scaled to match — leaving an `OrderEvent` trail that looks entirely normal. `quantity: 0` is the quiet variant: cooked, served, billed at zero. **This is the cheapest fix in this document and the shortest path to till fraud in the system.** | `OrderEndpoints.cs:168`, `:338`, DTO `:1387`; column `20260721184717_Slice3DiningArea.cs:122` · **security (C1)** | XS | ☐ |
| 0.4 | **Add a money test with real rates.** Every existing money test uses tax 0.05 / service 0.05 on round prices, so totals land exactly on cents *by construction* — which is precisely why `10.5588` shipped. Add one with `tax_rate = 0.14`, `service = 0.12`, `tax_on_service = true`. | `CloseAndReceiptTests.cs:26-28`, `CashierTests.cs:23-25` | XS | ☐ |

> **Why 0.1 before everything else:** it is the only finding in this report where *waiting
> actively destroys information*. Every other item is a defect that stays the same size.

---

## Phase 1 — Before this is deployed anywhere real

Cheap fixes to defects that are catastrophic if exploited.

| # | Item | Evidence | Effort | Status |
|---|---|---|---|---|
| 1.1 | **Close the privilege-escalation path.** `PUT /users/{id}` sets any role on any user in the tenant — **including the caller's own row** — with no check on the caller's role. Owner is an unconditional superuser. The default seed gives Manager every permission, so **Manager ⇒ Owner in one request in a stock install**. `PUT /roles/{role}/permissions` similarly rewrites any role's grants with no "cannot grant what you do not hold" check. Four guards needed: Owner assignable only by Owner; no self-modification of role or active flag; Owner's grants not editable; refuse to leave the tenant with zero active Owners. | `StaffEndpoints.cs:32`, `:52`, `:105-119`; `PermissionAuthorization.cs:12`; `DevSeeder.cs:86-89` · **security (C3)** | S | ☐ |
| 1.2 | **Delete `/dev/token`.** It mints an 8-hour Owner token for an *arbitrary* tenant with no credentials, guarded only by `IsDevelopment()`. That is one environment variable away from total, silent, unauthenticated compromise of every tenant — with no log line. `/auth/login` + `DevSeeder` now cover every use it had; the RLS endpoint tests can switch to `/auth/login` against seeded users. Ten minutes of work that removes the possibility entirely. | `Program.cs:126-174` · **security (H2)** | XS | ☐ |
| 1.3 | **Make `order_events` append-only at the database.** `GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES` covers `order_events`, and `ALTER DEFAULT PRIVILEGES` extends it to every future table. Grep for `TRIGGER\|REVOKE\|RULE` across `src/` and `scripts/` returns nothing — golden rule 4 is enforced by nobody reading the file. Separately, the FK is `ON DELETE CASCADE`, so **deleting an order deletes its entire audit trail**. `REVOKE UPDATE, DELETE`, add a rejecting trigger, change the FK to `RESTRICT`, and add a test asserting the UPDATE fails. | `scripts/002_create_app_role.sql:29`, `:34`; `20260721191358_Slice4FireBatch.cs:38-42` · found independently by **security (H3)** and **architecture (H3)** | S | ☐ |
| 1.4 | **Shorten the token and add a validity check.** 8-hour JWT, no refresh, no `jti`, no denylist, no server-side logout. `IsActive` is checked exactly once, at login; role is read only from the claim, never re-read. An employee fired at 14:00 keeps full Manager access until 22:00. Shorten to 15–60 min + refresh endpoint; add a per-request `IsActive`/role check (one indexed PK lookup inside the existing transaction), which closes the role-drift hole for free. | `AuthEndpoints.cs:86`, `:51`; `JwtTenantContext.cs:39-42`; `PermissionAuthorization.cs:13` · **security (H1)** | M | ☐ |
| 1.5 | **Audit staff, permission, and menu changes.** Every order mutation writes an `OrderEvent`. Creating a user, changing a role, deactivating a user, rewriting a permission matrix, and changing a price write **nothing**. Those are exactly the insider actions that enable fraud — so 1.1's escalation path is not merely unblocked, it is invisible. Add an `admin_events` table with the same shape and the same append-only enforcement as 1.3. | `StaffEndpoints.cs` (whole file), `MenuEndpoints.cs` (whole file) · **security (M4)** | M | ☐ |
| 1.6 | **Gate the money-path reads.** `/orders/open`, `/orders/{id}`, `/kds/.../tickets`, `/receipt`, and `/bill-preview` carry `.RequireAuthorization()` but **no permission gate** — while `/orders/history` and `/dashboard/summary` *are* gated behind `ViewHistory`, which makes the omission look accidental. Any authenticated principal, including a Kitchen-role wall display (the least physically secured device in a restaurant), reads every open order, every closed receipt with totals, and every bill preview in the tenant. | `OrderEndpoints.cs:395`, `:513`, `:650`, `:1118`, `:1180` · **security (M3)** | S | ☐ |
| 1.7 | **Prove RLS covers the tables that hold money.** The existing tests are better than most — they connect only as `pos_app`, and they cover the two failure modes people miss (unset context failing closed; pooled connections not leaking). But they prove **reads on 5 of 19 tables** — none of them `orders`, `order_lines`, `order_events`, or `menu_items` — and **every test is a `SELECT`**, so the `WITH CHECK` half of the policy is asserted nowhere. Add: a parameterized read test over the full table list, a rejected cross-tenant *write* test, and a schema assertion that every public table has `relrowsecurity AND relforcerowsecurity` — that last one turns the next slice's forgotten policy into a red build instead of a breach. | `Rls/RlsIsolationTests.cs` · **security (H4)** | M | ☐ |
| 1.8 | **Bound combo expansion.** `ComboItem.Quantity` is validated only as `>= 1` at order time and unbounded at attach time. A component with `quantity: 500000` builds half a million `OrderLine` objects in one transaction, with an `OrderByDescending` over the full array. Bound at attach (1–99) and add a total-units ceiling before expansion. | `MenuEndpoints.cs:294`, `OrderEndpoints.cs:271`, `:242`, `ComboAllocation.cs:66` · **security (M8)** | XS | ☐ |
| 1.9 | **Fix the deployment-config gaps.** Move the committed DB password out of base `appsettings.json` and rotate it (it is in git history); add `.env*`, `*.pfx`, `*.key`, `appsettings.*.Local.json` to `.gitignore`; add `UseForwardedHeaders` **before** `UseRateLimiter` (without it the login limiter is one global bucket and the first ten bad logins lock out the whole venue — the code comment says so and it is unresolved); move `UseCors` **above** `UseRateLimiter` so 429s carry CORS headers and stop surfacing as "could not reach backend"; add `UseHsts`/`UseHttpsRedirection`/`nosniff`; set `AllowedHosts`. | `appsettings.json:9`, `:6`; `scripts/002_create_app_role.sql:12`; `Program.cs:106-108`, `:180-182` · **security (L1, L6, L7, M7)** | S | ☐ |
| 1.10 | **Mint and validate `iss`/`aud`, and set `ClockSkew`.** Both validations are disabled and tokens carry neither claim, so any token signed with the key is accepted regardless of origin — a shared staging/production key makes staging tokens valid in production. The default 5-minute `ClockSkew` also silently extends every token. | `Program.cs:57-58`; `AuthEndpoints.cs:87` · **security (M5)** | XS | ☐ |
| 1.11 | **Add the four missing indexes.** *(Promoted from Phase 3 — the capacity review measured this and it is the single highest-leverage change in the document.)* All figures warm-cache on a 100-tenant / 695k-order / 4.2M-line / 8.3M-event database, queried as `pos_app` under real RLS: dashboard **932 ms → 0.34 ms (2,700×)**, history **959 ms → 0.24 ms (4,000×)**, KDS board **219 ms → 3.1 ms and 36.6 → 2,578 tps (70×)**, takeaway `dailyNo` 52 ms → 0.09 ms. Cost: 462 MB, 13 s to build over 12.5M rows. **This one change moves the small-instance tier from ~10 venues to several hundred.** Use `CREATE INDEX CONCURRENTLY` in production. <br>The root cause is that RLS *forces* `tenant_id` into every predicate while **no composite index leads with `tenant_id`** (only two single-purpose ones do), so the planner does a `BitmapAnd` of two low-selectivity indexes and discards 97% of rows. The policies themselves are fine — `current_setting()` is `STABLE`, folded once per statement, and fully indexable. <br>`order_events` has indexes on `tenant_id`/`order_id`/`order_line_id` and **nothing on `event_type` or `performed_at`, which is exactly what both consumers filter on.** | measured; `OrderEndpoints.cs:652-655`, `:452-457`, `:93-95`, `:397-400`; `DashboardEndpoints.cs:18-22`; `OrderEventConfiguration.cs:56-58`; `OrderLineConfiguration.cs:69` · **capacity (b#1, b#2)**, **architecture (H5)** | S | ☐ |
| 1.12 | **Set `Maximum Pool Size` and raise `max_connections`.** The connection string sets no pool size (Npgsql default **100**) and compose sets no `max_connections` (Postgres default **100**). **One app instance's pool exactly equals the entire Postgres connection budget** — instance #2 gets nothing, and neither does `psql` or the migration runner. This is a hard blocker on *all* horizontal scaling, independent of the backplane. Note the `SET LOCAL`-inside-a-transaction design is already **PgBouncer transaction-pooling safe**, which is a genuine architectural asset — fronting with PgBouncer is a valid answer here. | `appsettings.json`; `docker-compose.yml` · **capacity (b#3)** | XS | ☐ |

---

## Phase 2 — Make the product actually function

Phases 0 and 1 make it safe. **Nothing in this phase is optional for a real venue.**

| # | Item | Evidence | Effort | Status |
|---|---|---|---|---|
| 2.0 | **Ship a Dockerfile, a migration job, and a second node.** `find . -name "Dockerfile*"` returns **nothing** — Coolify would fall back to Nixpacks auto-detection on a multi-project .NET solution plus a 4-project Angular workspace. **The app is not deployable as-is.** Migrations are deliberately not applied on startup (correct — RLS DDL needs `pos_owner` while the app runs as `pos_app`), but there is no migration job in compose either, so it is a manual step against a single node with no blue/green. Add `ForwardedHeaders` here too (see 1.9). | verified absent · **capacity (§6, d#7)** | M | ☐ |
| 2.1 | **Break the close deadlock.** Close requires every line to be `Served` or `Voided`. Only `ServeLine` reaches `Served`, and it is granted to Waiter alone — a role with no app. So **100% of bills are unclosable**, tables never release, and after ~20 covers the `BR-C-03` unique index makes the floor permanently un-openable. Grant `ServeLine` to Cashier and add a per-line "Served" control to the bill review; the backend endpoint already exists. Also fix the copy: the blocked-state message says "Waiting on the kitchen — N items still cooking" while the count also includes `Ready` lines, so the cashier is told to wait on food already plated. | `OrderEndpoints.cs:1046`, `:789`, `:1207`; `DevSeeder.cs:97`; `order-review.html:85` · **usability** | S | ☐ |
| 2.2 | **Put the table number on the KDS ticket.** The ticket header shows `#3F9A` — the first four hex chars of the order GUID. No table, no waiter, no daily number; the ticket DTO does not even return them. **The kitchen cannot route plates**, which means a paper ticket rail runs alongside the product. It also prints `batch 7C2E` on the kitchen wall, which is an internal grouping key a cook has no concept for. | `OrderEndpoints.cs:665-681`; `ticket-card.ts:55`, `ticket-card.html:7`, `:11` · **usability**, **a11y (K2)** | S | ☐ |
| 2.3 | **Snapshot `item_name` onto `OrderLine`.** The line snapshots price, modifier names, and combo name — but not the item name, the one snapshot golden rule 6 most obviously requires. Consequences: **customer receipts print raw GUIDs** (`1x f549e822-23ef-… 5.16`), the KDS fetches the whole menu just to resolve names and falls back to `Item a1b2`, and every ticket read pays for it. One migration fixes three surfaces. | `OrderEndpoints.cs:1304-1327`, `:160-171`, `:285-299`; `kds-api.service.ts:18` · **architecture (M5)**, **usability** | S | ☐ |
| 2.4 | **Check availability at fire.** Availability is enforced at add-line, combo expansion, and increase-quantity — and **skipped at fire**, where `specs/03` explicitly requires it. The realistic sequence is exactly the one that breaks: waiter drafts salmon → kitchen 86's it → waiter fires → salmon goes to the pass. Highest-value missing guard in the domain. | `OrderEndpoints.cs:574-586`; contrast `:153`, `:260`, `:950` · **architecture (H2)** | XS | ☐ |
| 2.5 | **Add the three missing POS primitives.** All are hard dead ends today — not degraded, not manager-gated, simply absent. These are what make floor staff trust a till. <br>• **Order cancel** — `OrderStatus.Cancelled` exists in the enum and no endpoint ever assigns it. Walk-outs, duplicates, and wrong-table opens have no exit but closing a zero-line order, which writes a bogus `Completed` row into history and the day's count. <br>• **Un-bump** (`Ready → Preparing`) — a cook who fat-fingers `Ready` on a 40-minute steak has erased it from the board with no undo, and a manager cannot help either. <br>• **Reopen / void-sale** — close is one-way and voiding a completed order's lines is refused, so a wrongly-closed bill permanently diverges the drawer from the books. | `OrderStatus.cs:8`; `OrderEndpoints.cs:691`, `:742`, `:1035-1036`, `:834-835`; `station-board.ts:188` · **usability** | L | ☐ |
| 2.6 | **Make terminals survive their own token.** The 8-hour JWT expires mid-dinner by construction (open at 14:00 → expiry at 22:00). On 401 the interceptor calls `disconnect()`, which **nulls the session context**, so the connect screen cannot prefill — a cook must re-type three raw GUIDs and a password during service. Keep tenant/branch/station in a provisioning key that `disconnect()` does not clear, and prefill the station from the route. Pairs with 1.4. | `auth.interceptor.ts:33-36`; `session.service.ts:88-92`; `station-connect.ts:35-39` · **usability**, **a11y (M10)** | S | ☐ |
| 2.7 | **Add a Branch Settings screen** (GET/PUT + form) for tax rate, service charge, `TaxOnService`, currency, and printer IP/port. `BranchSettings` has **no GET, no PUT, and no UI** — tax is hardcoded 14%/12%/EGP in the seeder and changeable only by SQL. This is the single largest recurring SQL dependency for an operator. Note: validate the printer address with `TryParse` (today a hostname yields an unhandled `FormatException` → 500), and when the field becomes writable it is a blind-SSRF primitive — restrict to the branch's private range with a port allowlist. | `BranchSettings.cs:11-25`; `DevSeeder.cs:56-64`; `OrderEndpoints.cs:1253`, `:1274` · **usability**, **security (L9)** | M | ☐ |
| 2.8 | **Give `POST /users` credentials, and add password reset.** `CreateUserRequest(DisplayName, Role)` carries no username and no password, while login requires `PasswordHash: not null`. **Every account the Manager app creates is permanently unable to log in, and the UI gives no hint** — the worst kind of failure, because it looks like it worked. There is also no password-reset endpoint at all. | `StaffEndpoints.cs:136`; `AuthEndpoints.cs:51`; `staff.html:27-48` · **usability** | S | ☐ |
| 2.9 | **Build the waiter tablet.** There is no Dart file, no `pubspec.yaml`, nothing. Nine endpoints — open order, add line, add combo, patch line, fire, serve, void, increase-quantity, takeaway — have **zero frontend callers**. Roughly half the backend is written for a client that was never built, and the system's entire input path is missing. Build the undo surface (2.5) alongside it, because every dead end there becomes a customer-facing incident on the first busy night. | verified against every frontend service file · **usability** | XL | ☐ |
| 2.10 | **Split `/health` into `/health/live` and `/health/ready`.** The single endpoint aggregates Postgres **and Redis** — but Redis backs only the SignalR backplane, so a Redis blip marks the whole app unhealthy and **Coolify restarts a container that is perfectly able to take orders and print bills**, mid-service. Tag Redis non-critical. Also note `PostgresHealthCheck` uses `CanConnectAsync`, which draws from the same 100-slot pool (see 1.12). | `Program.cs:189`; `DependencyInjection.cs:56-59`; `HealthChecks/PostgresHealthCheck.cs:13` · **capacity (c#5)** | XS | ☐ |
| 2.11 | **Configure graceful shutdown and unbounded SignalR retry.** There is no shutdown timeout config and no SignalR drain. Combined with the client's 4-attempt reconnect cap (see 3.8), **every deploy risks permanently dark kitchen screens** — any restart longer than ~42 s leaves every KDS dead until someone manually reloads the browser. | `pos-hub.service.ts:51` · **capacity (§3, §6, c#7)** | S | ☐ |

---

## Phase 3 — Structural decisions that get more expensive with delay

Each of these is cheap now and a migration-plus-incident later. Decide explicitly, even if the
decision is "not in Phase 1" — an entry in `CLAUDE.md` counts as resolving it.

| # | Item | Evidence | Effort | Status |
|---|---|---|---|---|
| 3.1 | **Move the SignalR dispatch after COMMIT.** The comment claiming "broadcast after successful save — best-effort" is **false**: `TenantSessionMiddleware` opens the transaction before the endpoint and commits after it returns, so `SaveChangesAsync` only flushes and every `SendAsync` runs pre-COMMIT. Both consequences are inverted from the intent — a **Redis hiccup rolls back a successful fire**, and a COMMIT failure leaves **phantom KDS tickets** for lines that do not exist. It also holds a Postgres connection open across Redis round-trips on every fire, void, and bump. An endpoint filter owning commit-then-dispatch is ~30 lines; an outbox table is the shape you want for Phase 2 anyway. **Fix before the next realtime feature**, since each new event deepens the wrong coupling. | `OrderEndpoints.cs:616` (the wrong comment), `:608`, `:623`, `:715`, `:766`, `:889`, `:1000`; `TenantSessionMiddleware.cs:39-45` · **architecture (C1)** | M | ☐ |
| 3.2 | **Buffer the HTTP response until after commit.** Same root cause as 3.1: the endpoint writes status and body, *then* the middleware commits. The cashier can be shown `200 OK` with a closed bill and a total for a transaction that then fails to commit — screen says closed, database says open. | `TenantSessionMiddleware.cs:43-45`; `OrderEndpoints.cs:1104` · **security (M1)** | S | ☐ |
| 3.3 | **Decide what `branch_id` means, then enforce it or document that it means nothing.** RLS keys on `tenant_id` only and **no endpoint filters by branch**. Eight confirmed cross-branch paths including `/orders/open`, `/orders/{id}/close`, KDS bump, and `PosHub.JoinStationGroup`. The claim itself is freely chosen — login only checks the branch exists in the tenant, and there is **no user↔branch assignment table at all**. For a franchise this is a tenant-isolation failure in everything but the strict database sense. The durable fix is a second GUC + policy predicate, not 30 hand-written `Where` clauses (note `order_lines` has no `branch_id` and would need one). **This is the item most likely to grow expensive with every slice shipped on the current assumption** — and if the answer is "out of scope for Phase 1", correct the `CLAUDE.md` line promising protection that does not exist. | RLS policies in all migrations; `OrderEndpoints.cs:37`, `:397`, `:515`, `:652`, `:689`, `:740`, `:831`, `:1032`; `PosHub.cs:19`; `AuthEndpoints.cs:43` · **security (C2)** | L | ☐ |
| 3.4 | **Fix `daily_no`.** It is a lifetime `COUNT(*)` with no date filter (day 400 hands a customer ticket #11,842), it races (two concurrent takeaways get the same number, with no unique constraint to catch it), and it is an unbounded sequential count executed on every takeaway. `/orders/open` sorts by it, so duplicates corrupt the cashier queue too. Needs a per-`(branch, business_date)` counter taken with `UPDATE … RETURNING` and a `UNIQUE (branch_id, business_date, daily_no)`. Decide **before** real numbers are on real receipts, or you inherit a backfill problem. | `OrderEndpoints.cs:93-95`, `:399`; `OrderConfiguration.cs` (no index) · **architecture (C2)** | M | ☐ |
| 3.5 | **→ Promoted to 1.11.** The capacity review measured the gains (up to 4,000×) and confirmed this moves the small-instance tier by an order of magnitude. Do it in Phase 1. | — | — | → 1.11 |
| 3.6 | **Decide: is `OrderEvent` an audit log or an event store?** It is advertised as both and is neither. `Modified` and `Refired` are dead enum members; **add-line, patch-line, delete-line, and combo expansion write no event at all** — so order state cannot be rebuilt, and the log cannot answer "what was on this order before the waiter deleted two lines?" Pick one, write it in `CLAUDE.md`, and add the four missing draft-lifecycle events if it is an audit log (the right call for Phase 1). Do not drift into an event store by accident. Pairs with 1.3. | `OrderEventType.cs:7`, `:9`; `OrderEndpoints.cs:129-197`, `:373-392` · **architecture (H3)** | M | ☐ |
| 3.7 | **Promote the duplicated guards.** `CLAUDE.md`'s own rule is "promote at the fourth site." The row-version guard is written out character-identically at **eight**; the waiter broadcast block at four; the modifier bulk-load at seven; the permission-with-own-waiter-exception at two **and already diverging** (one lists Fired/Preparing/Ready/Served, the other Fired/Preparing). Drift is not hypothetical — `DELETE /lines/{lineId}` already takes `rowVersion` as a query-string int while every other site reads it from the body. Also: `Order.IsAcceptingChanges` was correctly promoted but the promotion **never reached two call sites**, which still restate the status list raw. Three static helpers in `Pos.Api` — no layers, no MediatR. | `OrderEndpoints.cs:148,226,318,378,571,846,932,1038`; `:725-731,776-782,816-822,897-903`; `DashboardEndpoints.cs:25`; `DiningAreaEndpoints.cs:89` · **architecture (H1)** | S | ☐ |
| 3.8 | **Make the KDS survive a network blip.** `withAutomaticReconnect()` uses the default schedule `[0s, 2s, 10s, 30s]` and then **stops permanently**, firing `onclose` which only sets a status string. There is no retry loop and no polling — a 45-second wifi drop freezes every kitchen screen forever until someone reloads the browser. The reconnect *reconciliation* is well built (it re-fetches authoritative state and preserves `firstSeenAt`) — it just never gets to run. Pass an infinite retry policy, add a 30s poll as a backstop, and connect the hub **before** the initial `reload()` (today `reload()` at `:78` completes before `connectToStation()` at `:80`, so anything fired in that window never appears). | `pos-hub.service.ts:51`, `:69`; `station-board.ts:72-80` · **architecture (H4)**, **usability** | S | ☐ |
| 3.9 | **Wire or delete the dead SignalR groups.** `JoinWaiterGroup` and `JoinBranchGroup` exist on the hub and **no client ever calls either**. So every `waiter:` broadcast — including `LineStatusChanged`, the single most time-critical signal in a restaurant ("food is ready") — is emitted into an empty group, and `AvailabilityChanged` never reaches a tablet, leaving BR-A-02 unimplemented on the client side. A broadcast nobody receives is worse than no broadcast: it reads as a working feature. <br> **Note:** `CLAUDE.md` open decision #4 and `models.ts:78-80` both claim bump events go *only* to `waiter:` and never to `station:` — **that is now false**; the station broadcast exists and the KDS does join it. Correct both. | `PosHub.cs:28`, `:46`; `OrderEndpoints.cs:715`, `:766` (station — works), `:725,776,816,897` (waiter — dead); `MenuEndpoints.cs:114` · **usability**, **architecture (H4)**, **a11y (K7)** | S | ☐ |
| 3.10 | **Validate client-supplied foreign keys.** `SectionId`, `CategoryId`, `StationId`, `ItemId`, `GroupId`, `ComboId`, `BranchId` are never checked for existence or ownership. The FK constraint is the only backstop — and PostgreSQL's referential-integrity triggers run **with RLS bypassed**, so an FK to another tenant's row would be *accepted*. Guessing a v4 GUID is infeasible, so this is not exploitable today; it becomes so the moment any endpoint leaks an id across the boundary. Under RLS a simple `AnyAsync` is already tenant-scoped — one query each. | `DiningAreaEndpoints.cs:44`; `MenuEndpoints.cs:83-84`, `:139`, `:294`, `:333` · **security (M6)** | S | ☐ |
| 3.11 | **Handle FK-violation on delete.** `MenuItem`/`Combo` delete calls `Remove` unguarded while the line FK is `Restrict`, and the exception handler only catches `UniqueViolation` — so deleting any item ever ordered returns a **500**. The dining-area endpoints already pre-check and return 409; copy that, or add a `ForeignKeyViolation` arm. | `MenuEndpoints.cs:125-132`; `OrderLineConfiguration.cs:56-59`; contrast `DiningAreaEndpoints.cs:66-67` · **architecture (M3)** | XS | ☐ |
| 3.12 | **Model discounts before they are needed.** `unit_price` already carries **two** meanings — list-price snapshot, and allocated share of a bundle — and `PATCH`/`increase-quantity` both grew explicit refusals to protect that invariant. A line-level discount is a *third* meaning and will not compose: a discounted combo component has no consistent price to store. Add `order_line_discounts` (or a per-line `discount_amount`) from the start; expect to touch `ComputeBill`, the receipt renderer, and the combo guards together. Same reasoning applies to split-bill, which needs a `Check`/`Bill` entity between `Order` and `OrderLine` — budget it as a slice, not a patch. | `OrderEndpoints.cs:329-333`, `:923-927`, `:1373`; `OrderConfiguration.cs:79-81` · **architecture (M6, section e)** | — | ☐ decide only |
| 3.13 | **Stop deriving dashboard and history metrics from the event log.** `count(DISTINCT order_id) WHERE event_type='Created'` over an append-only stream is the wrong shape at any scale — an index makes it fast, but the design still re-derives on every read. Add `created_at`/`closed_at` to `orders` so neither endpoint touches `order_events` at all, and/or maintain a daily rollup on write. This eliminates the #1 bottleneck *by design* rather than by index. | `DashboardEndpoints.cs:18-22`; `OrderEndpoints.cs:452-457` · **capacity (d#2, d#3)** | M | ☐ |
| 3.14 | **Partition `order_events` by month** (`RANGE` on `performed_at`) with a retention/archival policy. It is append-only and never pruned: **200M rows / 60 GB at 100 venue-years**, 2B rows / 600 GB at 1,000. Past ~10 GB it stops fitting in page cache and the growth curve stops being linear. | growth table below · **capacity (d#1)** | L | ☐ |
| 3.15 | **Narrow the transaction boundary to DB work only.** `next(context)` covers endpoint execution *and* the JSON write to the socket, so **a tablet on bad wifi holds an open Postgres transaction and a pooled connection for the duration of a slow body write**. With pool=100, ~100 slow clients is a total outage. The `SET LOCAL` design itself is right — this is about where the transaction ends. (Related to, but distinct from, 3.2.) | `TenantSessionMiddleware.cs:39-45` · **capacity (§2a, d#6)** | M | ☐ |
| 3.16 | **Add per-tenant rate limiting.** Only `/auth/login` is limited. Nothing constrains one tenant across the 46 mutating endpoints or any read endpoint — and the noisy-neighbour path below is measured at **~1 GB of I/O per dashboard load**, so one tenant's manager hitting refresh evicts every other tenant's working set from `shared_buffers`. | `Program.cs:109-122`; `AuthEndpoints.cs:96` · **capacity (§5, d#5)** | M | ☐ |

---

## Phase 4 — Quality, accessibility, and polish

Real work; nothing load-bearing. Grouped so related fixes land together.

### 4a — Design-system fixes (highest leverage per line changed)

| # | Item | Evidence | Effort | Status |
|---|---|---|---|---|
| 4.1 | **Swap `--sf-cyan` → `--sf-cyan-deep` on all primary surfaces.** `#0088b0` is **3.65:1** on the surface — it fails as text *and* as the background of every primary button in two apps, including **Close bill**. `--sf-cyan-deep #006786`, already the hover state, passes at **5.72:1**. One token change fixes ~15 call sites. Darken `--sf-muted-2` to ≈`#6b6767` (3.85:1) and `--sf-faint` to ≈`#767272` (2.59:1) in the same pass. | measured ratios in the a11y review · **a11y (§1)** | XS | ☐ |
| 4.2 | **Delete the three `outline: none` declarations.** The kit removes the focus ring from every text input, and because the global rule uses `:where(...)` (zero specificity), `.input:focus` wins. The replacement indicator is a glow at **1.06:1** — invisible. `DESIGN.md:28` claims always-visible focus rings; that claim is false for every input in the product. | `ui.css:121`; `category-panel.css:63`; `modifier-library.css:35` · **a11y (A)** | XS | ☐ |
| 4.3 | **`overflow: visible` on `.tabs`.** The container clips its own children's focus rings (drawn outside the border box with `outline-offset: 2px`), so a keyboard user tabbing into Menu or Staff has no idea where they are. | `ui.css:135` · **a11y (B)** | XS | ☐ |
| 4.4 | **Add live regions.** Grepping all 20 templates: **no `aria-live`, no `role="status"`, no `aria-busy`, no programmatic `.focus()` anywhere** — in a signals + SignalR product. New tickets, voids, connection changes, and bill closes are all silent to AT. The skeleton loaders are `aria-hidden="true"`, so a screen-reader user gets literal silence between navigation and content. One visually-hidden `<p role="status">` per screen, fed by the existing `loading()`/`connection()`/`tickets().length` signals. | `open-orders.html:15`; `history.html:16`; `dashboard.html:28,32` · **a11y (C)** | S | ☐ |
| 4.5 | **Render currency everywhere, from branch settings.** Money is rendered three ways: `currency:'EGP'` hardcoded, a raw number with no unit, and `number:'1.2-2'`. The cashier's grand total — the number read aloud to a customer — is a bare `342.50`. Depends on 2.7. | `category-panel.html:98`; `combo-panel.html:50`; `order-review.html:78`; `history.ts:66` · **a11y (F)** | S | ☐ |
| 4.6 | **Stop reusing status hues for non-`OrderLine` concepts.** `PRODUCT.md:33` says "one colour means one thing everywhere — the palette IS the OrderLine state machine." But order status maps onto line colours (`Open → badge--fired`, `Completed → badge--ready`) and table occupancy onto them too. Green now means *cooked*, *closable*, *table free*, and *completed*; magenta means both *just fired* and *open, not yet fired* — near-opposite meanings. | `history.ts:42-50`; `floor.html:110` · **a11y (E)** | S | ☐ |
| 4.7 | **Unify the three form vocabularies and extract `extractError`.** Floor and Staff use the kit properly; all three Menu panels use bare implicit labels with locally re-invented styles. Separately, `extractError` is copy-pasted verbatim into **7** Manager components and exists in **neither** Cashier nor KDS, which `catch { }` and discard the status. One exported helper in `shared`. | `category-panel.html:11-18`; `combo-panel.html:14-21`; `modifier-library.html:14-29`; 7 × `extractError` · **a11y (D, G)** | S | ☐ |

### 4b — KDS physical fitness

> **Verdict: not fit for a 2m wall-mounted kitchen screen.** This is the app with the most
> design intent and the widest gap between intent and physics.

| # | Item | Evidence | Effort | Status |
|---|---|---|---|---|
| 4.8 | **Size the KDS for the wall and move off the serif.** At 43″/1080p (0.496 mm/px), comfortable cap-height at 2m is ~10mm. Item names are **5.6mm**, qty/timer **5.9mm**, and modifiers ("No onions") **4.3mm — unreadable**. The station name (11.2mm) is the *only* element sized for the wall, and it is the one constant a cook learns in a shift. Item names need ~30px, modifiers ~26px. Compounding it, the UI face is **Source Serif 4** — high stroke contrast and thin brackets, the classic wrong choice for distance viewing under glare. | `ticket-card.css:78`, `:74`, `:84`, `:31`, `:37`; `service-spruce.css:64` · **a11y (K4)** | M | ☐ |
| 4.9 | **Bound the board and order it by urgency.** The board scrolls (`overflow-y:auto`) — 30 tickets overflow a 1080p viewport on a panel with no mouse, no keyboard, and possibly no touch, so tickets past the fold are **unreachable**, with no paging and no "+N more". And it is not urgency-ordered: the query sorts by line sequence *within its order*, so every order starts at 1 and cross-order ordering is arbitrary — a 25-minute ticket can sit at position 14. Worse, the aging timer **resets to `00:00` calm-cyan on any reload**, because unseen tickets get `firstSeenAt = Date.now()`. Returning `firedAt` on the ticket DTO and sorting by it server-side fixes both the ordering and the lying timer. | `station-board.css:106-114`; `OrderEndpoints.cs:664`; `station-board.ts:110-115`; `models.ts:33-36` · **a11y (K1, K2)** | M | ☐ |
| 4.10 | **Make a dead board unmistakable.** The entire disconnected signal is a **9px dot** (4.5mm at 2m) plus a 13px label — and the failure pair is red/green. No banner, no dimming, no overlay. A cook works a frozen board for the rest of service. Render a full-width `--sf-kds-voided` bar at 34px ("BOARD OFFLINE — NOT LIVE") and desaturate the grid. Pairs with 3.8. | `station-board.html:6-9`; `station-board.css:48-54`; `pos-hub.service.ts:69` · **a11y (K3)** | S | ☐ |
| 4.11 | **Enlarge the bump targets and handle remote bumps gracefully.** `Ready` is irreversible from this surface and sits on a **40px target with a 12px gutter** — at 0.5mm/px that is a 20mm target on a 26mm pitch, and a nitrile-gloved fingertip patch is 15–20mm. Exactly one fingertip wide, with the neighbour half a fingertip away. Passes SC 2.5.8, fails SC 2.5.5 and the physical brief. Go to 64px minimum with a 24px+ gutter. Separately, a remote bump silently morphs the button under the cook's finger **Start → Ready** (semantics invert in place), and a remote `Ready` makes the line vanish with no animation — while the *void* path gets a deliberate 1400ms flash-and-collapse precisely because the brief says a disappearance must never be silent. Same reasoning, never applied. And removing ticket #4 reflows every ticket after it, sliding #8 under a finger reaching for #7. | `ticket-card.css:89-101`, `:62-69`, `:106`, `:131-134`; `station-board.ts:185-207`; `DESIGN-BRIEF.md:210` · **a11y (K6, K7)** | M | ☐ |
| 4.12 | **Give ticket state a non-colour signal.** The ticket-level Fired/Preparing state is exposed **only** as an 8px coloured spine; the `NEW` chip that would carry it as text lives for 1200ms then becomes `batch XXXX`. After 1.2s the whole-board glance the code comment advertises is carried by hue alone — contradicting `PRODUCT.md:30`. Add a shape difference or a permanent 26px state word. Also: a voided line drops to **3.18:1** during the 1400ms flash because the dimming uses `opacity` rather than explicit colour tokens. | `ticket-card.ts:32-37`; `ticket-card.css:12-21`, `:106`; `station-board.ts:23,155` · **a11y (K5, K8)** | S | ☐ |

### 4c — Cashier

| # | Item | Evidence | Effort | Status |
|---|---|---|---|---|
| 4.13 | **Add a confirm to Close bill, carrying the amount.** One tap closes a bill, snapshots totals, no undo — while deleting a **kitchen station** has a two-step Delete → Confirm. The risk calibration is exactly inverted. Match the house pattern the Manager already uses: "Close bill · EGP 342.50" → "Confirm close". | `order-review.html:87-91`; contrast `staff.html:97-101`, `floor.html:75-82` · **a11y (C1)** | S | ☐ |
| 4.14 | **Make the open-orders list live.** "Can I close this yet?" is called the single most important cashier signal in the brief — and it is fetched **once** in `ngOnInit`, with no polling and no SignalR. A cashier will never see an order flip to "Ready to close" without pressing a 28px Refresh button. Subscribe to the branch group (the channel already exists) or poll every 10s; keep the manual Refresh either way. | `open-orders.ts:21-39`; `open-orders.html:4` · **a11y (C2)** | S | ☐ |
| 4.15 | **Handle 403 / 429 / 401 distinctly.** Only 409 is handled (correctly). Everything else says "Could not close the order. Please try again." — which is **false for a 403** (the cashier will tap forever) and **invites the exact behaviour that caused a 429**. A 401 is swallowed by the interceptor, so the bill vanishes mid-transaction with no message. `session.service.ts:134-152` already does this properly for login; the same logic needs to exist in-session. | `order-review.ts:86-95`; `open-orders.ts:34`; `auth.interceptor.ts:33-36` · **a11y (C4)** | S | ☐ |
| 4.16 | **Enlarge the grand total; add `<main>`; add focus management.** The total is 18px against 14px line items — a 1.29× step for the one number the cashier takes money against (go to ~28px). Neither shift-long screen has a `main` landmark (the connect screens do). And on close→receipt the focused button is destroyed with focus falling to `<body>` and nothing announced. | `order-review.css:115-121`; `open-orders.html:1-2`; `order-review.ts:78-98` · **a11y (C3, C5, C6)** | S | ☐ |

### 4d — Manager

| # | Item | Evidence | Effort | Status |
|---|---|---|---|---|
| 4.17 | **Fix the permissions-matrix data loss.** Saving one role re-fetches and `setValue`s **every** control for **every** role. Tick five permissions on Manager and three on Cashier, save Manager → Cashier's three silently revert, with no warning and no visual cue. Per-row Save buttons that look independent, aren't. **This is a correctness bug, not an a11y bug — fix it first regardless of audience.** | `staff.ts:166-183`, `:156` · **a11y (M1)** | S | ☐ |
| 4.18 | **Add validation messages.** **18** submit buttons are gated on `form.invalid` and grepping every template for `.errors`/`hasError`/`touched` returns **zero matches**. A manager who clears a required field sees a greyed-out Save and nothing explaining why. Fails SC 3.3.1 and 3.3.3, and is a dead end for sighted users too. | `staff.html:43`; `floor.html:29`; `category-panel.html:20`; +15 more · **a11y (M2)** | M | ☐ |
| 4.19 | **Bind `aria-current` to the active route.** It is a **static attribute on all five nav links**, so AT users hear "current page" on every one simultaneously. The visual active state is correct; only the AT-facing one is broken. | `app.html:10-14` · **a11y (M4)** | XS | ☐ |
| 4.20 | **Fix or drop the tab and disclosure ARIA.** `role="tablist"`/`role="tab"` are declared with **no `tabpanel`, no `aria-controls`, no roving `tabindex`, no arrow keys** — and Staff's tabs have **no `aria-selected` at all**. Either implement the full APG pattern or use plain buttons + `aria-pressed` (honest, and works today). Separately, the modifier-group disclosure has **no `aria-expanded`**, responds to **Enter only (not Space)**, and nests Edit/Delete buttons *inside* a `role="button"` — which AT flattens. And `role="grid"` on the permissions table overrides correct `scope`-based headers with a contract that isn't implemented; just delete it. | `menu.html:12-22`; `staff.html:12-15`, `:124`; `modifier-library.html:64`, `:68` · **a11y (M5, M6, M7)** | M | ☐ |
| 4.21 | **Add unsaved-change protection.** No `CanDeactivate`, no `beforeunload`, no dirty tracking anywhere. Switching Menu tabs, clicking a sidebar link mid-edit, or a 401 all discard silently. | grep: zero matches across all `.ts` · **a11y (M3, M10)** | S | ☐ |
| 4.22 | **Small fixes:** `aria-label` on the `✕` tag-cancel button (its sibling `.tag-detach` already has one — copy it); `<h2>`/`<h3>` structure on the Menu page (12 categories and 80 items currently share **one** heading); a dismiss button on the one error banner missing it; `<caption>` on the bill table. | `category-panel.html:129`, `:131`, `:46`, `:97`; `combo-panel.html`; `order-review.html:28` · **a11y (M8, M9, M11, C7)** | S | ☐ |
| 4.23 | **Role-filter the Manager nav.** All five links render unconditionally and any account can sign in, so a Cashier sees Staff, clicks it, and gets a raw 403 banner they cannot act on. Gate on `session.context().role`. | `app.html:10-14`; `StaffEndpoints.cs:17` · **usability** | XS | ☐ |
| 4.24 | **Fix the dashboard day boundary and add revenue.** The summary uses `clock.UtcNow.Date`; at UTC+2/+3 (the stated target market) everything after local midnight lands on the previous day. And there is **no revenue figure anywhere in the product** — only two counts. Make the boundary a branch setting. | `DashboardEndpoints.cs:152`, `:164-169`; `dashboard.html:20` · **usability** | S | ☐ |
| 4.25 | **De-jargon the UI.** `batch 7C2E` on the kitchen wall; "Placed since midnight (UTC)" to a restaurant manager; the raw `Available/OutOfStock/Hidden` enum (the industry word is "86'd", and the `Hidden` vs `OutOfStock` distinction is undocumented in the UI); "Stale row version" leaking to any consumer that hasn't translated it; raw GUIDs as the first thing any employee sees on every connect screen. | `ticket-card.html:11`; `dashboard.html:20`; `category-panel.html:102-104`; `OrderEndpoints.cs:149` +6 · **usability** | S | ☐ |
| 4.26 | **Add a "copy station id" affordance on the Floor page.** A cook must hand-type three raw GUIDs to reach the board, and the station id is **displayed nowhere in any UI** — so a manager cannot even read it out to write on a sticker. Devtools or `psql` is the current answer. | `station-connect.html:13-25`; `floor.html:172` · **usability** | XS | ☐ |
| 4.27 | **Make VOID require acknowledgement.** The void flash is **1400ms**, then the line vanishes. Spec §4 demands "never a silent disappearance"; 1.4s in a hot kitchen is silent. Also: there is **no audio anywhere in the frontend** — zero `Audio`, `.play()`, or notification calls. A kitchen board that only signals visually will be missed. | `station-board.ts:24` · **usability**, **a11y (K8)** | S | ☐ |

### 4e — Housekeeping

| # | Item | Evidence | Effort | Status |
|---|---|---|---|---|
| 4.28 | **Cache the permission lookup.** `HasPermissionAsync` issues a DB query per gated request; 42 endpoints pay it. | `PermissionAuthorization.cs:10-14` · **architecture (L1)** | XS | ☐ |
| 4.29 | **Consolidate the five divergent `OrderLine` projections** in one file (tracking, KDS, receipt, bill-preview, fire broadcast). Only receipt and bill-preview carry combo provenance, so `GET /orders/{id}` cannot distinguish a combo component from a mispriced line. Same entity, five shapes, no shared DTO. Also: contract interfaces are re-declared per app rather than shared. | `OrderEndpoints.cs:528-541`, `:669-680`, `:1142-1159`, `:1210-1225`, `:627-637`; `models.ts:4` vs `cashier-api.service.ts:88` vs `manager-api.service.ts:23` · **architecture (M4, L2)** | S | ☐ |
| 4.30 | **Scope the auth interceptor to the API origin.** It attaches the bearer token to **every** outbound request with no host check. Latent today (no third-party calls exist), a credential leak the moment one is added. | `auth.interceptor.ts:10-14` · **security (L4)** | XS | ☐ |
| 4.31 | **Reconcile the docs with the code.** `specs/06` says every route is under `/api/v1`; **no route has a prefix**, and several names differ (`/start-preparing` vs `/start`, `/serve` vs `/served`). Three front-ends already depend on the unprefixed routes, so amend the spec — but note `POST /orders/{id}/cancel` is specced, state-machined, and **genuinely missing** (see 2.5). Separately `DESIGN.md` describes a design system that no longer exists: brand "spruce #0F5F52 / mint #34D8B4" vs shipped cyan `#0088b0`; "Rubik — all UI" vs shipped Source Serif 4; radii 12px/8px vs shipped 2px/2px. | `specs/06-api-contracts.md:6-7`, `:265`; `OrderEndpoints.cs:19`; `DESIGN.md:9`, `:15`, `:25`; `service-spruce.css:64` · **architecture (M1)**, **a11y (H)** | S | ☐ |
| 4.32 | **Derive `DiningTable.Status`** rather than hand-maintaining it in two write paths; a third path (cancel, move) must remember both. | `OrderEndpoints.cs:69`, `:1078` · **architecture (L3)** | S | ☐ |
| 4.33 | **Stop echoing attacker input in claim-parse errors**, and fix the `IPAddress.Parse` 500 on a hostname. Both minor; both one line. | `InvalidTenantClaimsExceptionHandler.cs:31`; `JwtTenantContext.cs:60`, `:72`; `OrderEndpoints.cs:1274` · **security (L8, L9)** | XS | ☐ |

---

## Capacity & scale

Unlike the other four reviews, this one **measured**. Rig: 8 vCPU / 7 GB host, Postgres 16 at
stock settings, scratch DB built from a `pg_dump --schema-only` of the real dev schema — so all
RLS policies, indexes and FKs are the production ones. Loaded 100 tenants / 695k orders / 4.17M
`order_lines` / 8.34M `order_events`, queried as `pos_app` (non-superuser) inside
`BEGIN; SET LOCAL app.tenant_id=…` — byte-for-byte what the middleware does.

Tags: **[M]** measured · **[D]** derived arithmetic · **[E]** estimated judgement.

### Capacity table

| | **Tier A** — small instance (2 vCPU / 4 GB, all-in-one) | **Tier B** — tuned single node (8 vCPU / 32 GB, indexed) | **Tier C** — horizontally scaled |
|---|---|---|---|
| **Venues** | **6–12** [D] | **400–800** [E] | **3,000–5,000** [E] |
| **Concurrent users / devices** | 45–85 | 2,800–5,600 | ~35,000 across 8–12 instances |
| **Orders / hour** | 250–500 | 16,000–32,000 | 120,000–200,000 |
| **HTTP req/s** | ~10 | ~650 | ~4,000 |
| **Limiting factor** | `order_events` seq scans (932–959 ms, ~1 GB I/O per dashboard/history call) + KDS board at 36.6 tps [M] | Single Postgres primary write path; single-instance availability; `order_events` at ~1.6B rows needs partitioning | Single write primary (~1,550 write-txn/s at 5,000 venues [D]); `order_events` at 2B rows / 600 GB |

> **Read Tier A carefully.** Those numbers are for an *aged* database. On a fresh one the same
> 2 vCPU box handles 50+ venues. Capacity here is **history-dependent, not load-dependent** — it
> falls over after ~3–5 venue-years of accumulated events regardless of concurrent load.
> Tier C needs 1.11 + 1.12 before it is possible at all; the Redis backplane is already fine.

### Bottleneck ranking

| # | What breaks | At what load | Fix |
|---|---|---|---|
| **1** | `/dashboard/summary` + `/orders/history` parallel seq-scan **all of `order_events`, all tenants**, with RLS filtering *after* the scan — 130,940 buffers ≈ **1 GB I/O to return 78 rows** [M] | ~5–10M events ≈ **3–5 venue-years** | 1.11, then 3.13 |
| **2** | KDS board scans 200,000 rows to return 24 — 219 ms, **36.6 tps** at 8 concurrent [M] | ~200k lines/station ≈ **1 venue-year** | 1.11 |
| **3** | Npgsql pool (100) **==** Postgres `max_connections` (100) | **Instance #2, immediately** | 1.12 |
| **4** | Takeaway `dailyNo` = `COUNT(*)` over every takeaway order ever, on every create — 52 ms at 200k [M] | unbounded growth | 1.11 + 3.4 |
| **5** | `/orders/open` scans all tenant orders — 31 ms at 200k [M] | unbounded growth | 1.11 |
| **6** | Menu uncached and refetched on **every KDS bump**; nothing cached or compressed anywhere | ~3× KDS read amplification | 5.1, 5.2 |
| **7** | Per-request `BEGIN`/`SET LOCAL`/`COMMIT` — a **3.2× throughput tax**, 45,783 → 14,306 tps, +0.384 ms [M] | irrelevant at 800 req/s | 3.15 (boundary, not the design) |

### Growth model

22 events per order [D] from the actual code paths. Row size measured at 186 B with an empty
`details`; real payloads carry fire-batch line-ID arrays and close totals, so the table below
uses a corrected ~300 B/row — that correction is **[E]**, not [M].

| Venues | `order_events` rows/yr | Size | `order_lines` rows/yr | Size | Dashboard scan [D] |
|---|---|---|---|---|---|
| 1 | 2.0 M | 0.6 GB | 0.55 M | 125 MB | ~0.2 s |
| 10 | 20 M | 6 GB | 5.5 M | 1.3 GB | **~2.2 s** |
| 100 | 200 M | 60 GB | 55 M | 12.5 GB | **~22 s** |
| 1000 | 2.0 B | 600 GB | 548 M | 125 GB | **~220 s** |

Scan times extrapolate linearly from the measured 932 ms and are **optimistic** — past ~10 GB the
table stops fitting in page cache.

### Remaining capacity work

| # | Item | Evidence | Effort | Status |
|---|---|---|---|---|
| 5.1 | **Cache the menu per tenant** (`HybridCache`), invalidated from the availability broadcast **that already exists and is unused** for this. Largest repeated read in the system. | `MenuEndpoints.cs:63-66`, `:109-119` · **capacity (c#4)** | S | ☐ |
| 5.2 | **Stop the KDS refetching board + full menu after every local bump.** `reload()` issues **three** GETs including the entire menu, and runs after every bump — while the optimistic update has already applied the change. ~3× less KDS read traffic. | `station-board.ts:99-103`, `:201`, `:213` · **capacity (c#6)** | XS | ☐ |
| 5.3 | **Add `AsNoTracking()` to the ~20 read-only endpoints.** It appears **zero times in the entire codebase** [M] — every read materialises tracked entities into the change tracker and throws them away. 10–30% CPU/allocation reduction on read paths [E]. | grep: no matches · **capacity (c#3)** | S | ☐ |
| 5.4 | **Add response compression and paginate the unbounded reads.** No compression, no output caching, no response caching anywhere. `/orders/open`, `/users`, `/tables`, `/menu-items`, `/combos` are unbounded (only `/orders/history` is clamped, 1–500). Also `/menu` is **7 sequential round-trips** with no `Include`/`AsSplitQuery` plus O(items × groups) in-memory nesting. | `MenuEndpoints.cs:361-375`, `:391-392`; `OrderEndpoints.cs:450` · **capacity (§2d, §4)** | M | ☐ |
| 5.5 | **Add read replicas** for `/orders/history` and `/dashboard/*` — the only genuinely heavy reads, and both perfectly staleness-tolerant. | · **capacity (d#4)** | L | ☐ |

### Load-test plan

Replaces the [E] tiers above with measurements in about a day. Tooling: **k6** for HTTP, a
**`@microsoft/signalr` Node harness** (or `xk6-signalr`) for WebSockets, **pgbench** for isolated
query work, with `pg_stat_statements` + `auto_explain` (`log_min_duration = 200ms`) on throughout.

| # | Scenario | Pass / fail |
|---|---|---|
| 1 | **KDS board vs. history growth** — seed 1M / 10M / 50M `order_lines`, 50 VUs on the tickets endpoint | p95 < 150 ms at **all three** volumes. *Fails today at 1M* [M] |
| 2 | **Dashboard/history vs. event growth** — seed 5M / 50M / 200M events, 5 VUs | p95 < 500 ms **and** no `Seq Scan` in `auto_explain`. *Fails today at 8M* [M] |
| 3 | **Dinner-service mixed writes** — 50 venues, full lifecycle at 40 orders/hr each | p95 write < 300 ms, errors < 0.1%, **zero unexpected 409s** (row_version conflicts must stay genuine races) |
| 4 | **SignalR fan-out + reconnect storm** — 5,000 connections, kill an instance mid-run | All clients reconnect < 60 s, **zero permanently-dead clients** (directly tests the 4-attempt cap), no message loss through the backplane |
| 5 | **Noisy neighbour + pool saturation** — tenant A loops dashboard/history while tenant B runs scenario 3; separately hold 120 slow-reading clients | Tenant B p95 degrades **< 20%**; pool exhaustion returns a clean 503 rather than hanging. *Both fail today* [M] |

---

## Needs runtime verification

None of these can be settled by reading source. **The first one is the most important open
question in this document** — it determines whether tenant isolation holds in production at all.

| # | Question | How to check | Status |
|---|---|---|---|
| V1 | **Does `002_create_app_role.sql` actually run in a real deploy?** It runs in the test fixture, but appears in no deployment manifest, Dockerfile, or entrypoint. If it is skipped, the app connects as `pos_owner` from `docker-compose.yml:7` — a superuser, and therefore `BYPASSRLS`-capable. | `SELECT current_user, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user;` from a live app connection | ☐ |
| V2 | **What is `ASPNETCORE_ENVIRONMENT` on the Coolify target?** Everything about `/dev/token` hinges on this single value. (Moot once 1.2 lands.) | inspect deploy env | ☐ |
| V3 | **Is `ForwardedHeaders` configured at the proxy layer?** If not, the login limiter is one global bucket and ten bad logins lock out the venue. | inspect proxy config; verify `RemoteIpAddress` varies per client | ☐ |
| V4 | **Does `ALTER DEFAULT PRIVILEGES` cover tables from later migrations?** It applies only to tables created by the *same role* that ran the ALTER. Fails safe (a 500, not a leak), but confirm after the next migration. | run a migration, then query the new table as `pos_app` | ☐ |
| V5 | **Does TLS terminate before the API, and does the proxy log full URIs?** The SignalR `access_token` travels in the query string (standard, and correctly scoped to `/posHub`) — but query strings land in access logs, and with a non-revocable 8h token a leaked log line is a working session. | inspect proxy log format | ☐ |
| V6 | ~~Is a Redis SignalR backplane wired?~~ **Answered: yes.** `AddSignalR().AddStackExchangeRedis()` at `DependencyInjection.cs:77-78`, configured via `IOptions<RedisOptions>` at `:70-75`, Redis in `docker-compose.yml:19-30`. The parameterless overload is used deliberately to avoid an early-config-capture bug (comment at `:61-69`). **Multi-instance SignalR works today** — the blocker on horizontal scaling is the connection pool (1.12), not the backplane. | — | ☑ |
| V7 | **KDS legibility on the real panel.** The 4b numbers assume a 43″ 1080p panel at 2m (0.496 mm/px). Re-run against the actual hardware and viewing distance before committing to type sizes. | measure the panel; the contrast script is re-runnable | ☐ |

---

## Documentation corrections

Three claims in the project's own docs are now false. They matter because each one would lead a
future contributor to a wrong conclusion about how the system behaves.

| # | Claim | Reality | Where to fix | Status |
|---|---|---|---|---|
| D1 | `CLAUDE.md`: *"Isolation is enforced by **both** EF global query filters **and** PostgreSQL RLS."* | **There are zero `HasQueryFilter` calls in the codebase** — `grep -rn HasQueryFilter src/` returns nothing [M]. It is RLS alone. Isolation still holds, but the belt-and-braces claim is wrong, and the planner gets no help from EF. | `CLAUDE.md` (Multi-tenancy section) | ☐ |
| D2 | `CLAUDE.md` open decision #4: *"bump events broadcast **only** to `waiter:{id}`, never to `station:{id}`, so a second KDS terminal does not see another terminal's bumps."* | **Stale — the code does both.** `OrderEndpoints.cs:715` and `:766` each send `OrderLineStatusChanged` to `station:{StationId}` *and* `LineStatusChanged` to `waiter:{WaiterId}`. A second KDS on the same station **does** see live bumps. Confirmed independently by the accessibility and capacity reviews. The mirror of this stale comment in `models.ts:78-80` needs the same fix. The *real* gap is the opposite one: nothing ever joins `waiter:` (see 3.9). | `CLAUDE.md` #4; `models.ts:78-80` | ☐ |
| D3 | `OrderEndpoints.cs:616`: *"Broadcast after successful save — fire is authoritative, broadcast is best-effort."* | Inverted. The broadcast runs **inside** the uncommitted transaction, so it is authoritative enough to cancel a fire. A wrong comment on the money/kitchen path is worse than no comment. | fix with 3.1 | ☐ |

Lower-stakes drift is tracked as 4.31 (`specs/06` route prefixes and names) and the `DESIGN.md`
brand/typography/radii divergence.

---

## Do not regress — verified correct

Recorded deliberately, because the risk with a report this long is that someone "fixes" the
parts that are already right.

**Backend**

- **RLS is real and load-bearing.** All 19 tables carry `ENABLE` *and* `FORCE ROW LEVEL SECURITY`
  with both `USING` and `WITH CHECK`. `pos_app` is genuinely `NOSUPERUSER … NOBYPASSRLS`, and the
  tests connect as that role only.
- **`NULLIF(current_setting('app.tenant_id', true), '')::uuid` is exactly the right idiom** — it
  fails closed for both "never set" and "unset this session", and the non-obvious reason (empty
  string, not NULL, after any session has set the GUC) is written down at
  `20260721110835_InitialCreate.cs:124`.
- **`SET LOCAL` inside the request transaction** is the correct choice over a reset-on-return
  pattern — Postgres discards it at COMMIT, so there is no reset path to get wrong. The
  pooled-connection leakage test (`RlsIsolationTests.cs:99`) is the one most codebases skip.
- **`ExecuteSqlRaw` at `TenantSessionSql.cs:20` is safe** and is the only raw SQL in `src/`;
  `Guid.ToString("D")` cannot produce a SQL metacharacter and `SET` genuinely rejects bound
  parameters. Every other query is LINQ. No injection anywhere.
- **`GET /branches` deliberately ships with no `WHERE tenant_id`**, proving isolation does not
  depend on application code getting a filter right.
- **Password hashing:** PBKDF2-SHA256, 210k iterations, 16-byte salt, `FixedTimeEquals`, iteration
  count bounded against a tampered row.
- **The login endpoint closes both enumeration oracles deliberately** — dummy-hash verify for
  absent users kills the timing channel, and judging credentials *before* branch validity stops it
  being an oracle for which branches exist. The comments explaining why are what keep a future
  contributor from "simplifying" it.
- **Offline idempotency is correct**, and correctly ordered: the client-supplied id is checked
  *before* the row-version guard, so a replay of an applied write succeeds instead of 409-ing.
- **Optimistic concurrency is doubly enforced** (manual compare + `IsConcurrencyToken`) and
  correctly scoped — deliberately not bumping `RowVersion` on KDS status changes stops the kitchen
  false-conflicting with the waiter.
- **Price and modifier snapshotting works**; `increase-quantity` deliberately bills at the original
  line price; `DiscountAmount` is hardcoded so a client cannot inject a discount.
- **Combo pricing cannot be client-manipulated** — the request carries only `ComboId`, and
  `ComboAllocation` is a correct largest-remainder allocation that sums exactly to the bundle price,
  with 14 tests covering free components, awkward prices, and quantity-3.
- **The fire-batch model is correct and cheap** — BR-O-03 holds by construction, not by guard, and
  the KDS ticket really is a pure view with no `KitchenTicket` table.
- **`MapInboundClaims = false`** with the reason documented — an easy trap to fall into with `role`.
- **`PosHub` verifies group targets under RLS** before joining rather than trusting the client's id,
  and derives the waiter group from claims rather than a parameter. Only the branch dimension is
  missing (3.3).
- **Bearer tokens in headers ⇒ no CSRF surface**; `AllowCredentials` is paired with an explicit
  origin list, not a wildcard.

**Performance & infrastructure** *(measured, not assumed)*

- **The RLS policies are fully indexable and need no rewriting.** `current_setting()` is `STABLE`,
  so Postgres folds it once per statement, not per row — proven by plans becoming `Index Scan`
  with the RLS predicate as the index condition once a `tenant_id`-leading index exists. The
  problem is missing indexes (1.11), *not* the policy design.
- **`SET LOCAL` inside the transaction is PgBouncer-transaction-mode compatible.** Because tenancy
  rides on a transaction-scoped `SET LOCAL` rather than a session-level `SET`, transaction pooling
  works unmodified. That is a real architectural asset for horizontal scaling — do not "simplify"
  it to a session variable.
- **SignalR is correctly excluded from the tenant middleware** (`TenantSessionMiddleware.cs:23`).
  That exclusion is load-bearing.
- **The Redis SignalR backplane is wired and working** (see V6), with the parameterless overload
  chosen deliberately to dodge an early-config-capture bug.
- **No sync-over-async anywhere** — `grep` for `.Result` / `.Wait()` / `.GetAwaiter().GetResult()`
  returns nothing. Redis connects via `Lazy<Task<T>>` + `ConnectAsync`, deliberately non-blocking.
- **No true N+1 loops.** The code consistently uses the `ToHashSet()` + `Contains()` batch pattern
  for related-entity loads. This is done well and is worth preserving as the house style.
- **`IX_orders_table_id`** — the partial unique index enforcing BR-C-03 is genuinely well designed
  and doubles as the fast table→active-order lookup. It is the one hot path that already has the
  right index.
- **Migrations are deliberately not applied on startup**, because RLS DDL needs `pos_owner` while
  the app runs as `pos_app`. That is correct; it just needs a migration job (2.0).

**Frontend**

- **No XSS sink anywhere** — no `innerHTML`, no `bypassSecurityTrust*`, no `eval` under `projects/`.
- **A real global `prefers-reduced-motion` block** neutralises the KDS rise-in, void flash,
  connection blink, and skeleton shimmer, with an explicit static fallback for `.skeleton`.
- **The badge pattern** (state-coloured pip + always-ink label on a tint) is a textbook solution to
  "never colour alone" — it is why every badge label passes at 12.8:1+.
- **Logical properties used consistently** across all 18 stylesheets — the RTL groundwork is real
  even though RTL is not wired up.
- **Wide content scrolls inside its own container**; the page body never scrolls horizontally.
- **`session.service.ts:99-106`** proactively drops an expired token on restore rather than letting
  the app boot and 401-storm.
- **The KDS reconnect *reconciliation* is well built** — it re-fetches authoritative state and
  preserves `firstSeenAt` so aging timers survive a drop. It just needs the chance to run (3.8).
- **`station-board.ts:218-227`** fixes a real bug (reporting a bump failure *after* the reload that
  would have cleared it) with a comment explaining why.
- **The cashier's 409-means-already-closed path** transparently fetches the receipt instead of
  erroring. Print is correctly unguarded — reprinting is harmless and gating it would slow the till.
- **The disabled-Close + amber "waiting on kitchen" hint** is a clean implementation of the brief's
  key affordance, with a non-colour pip.
- **The Manager surfaces real RFC 9457 `title`/`detail`** rather than swallowing errors, and applies
  a two-step destructive confirm consistently across Menu, Floor, and Staff.

---

## Source reviews

| Review | Agent | Findings |
|---|---|---|
| Security | Application Security Engineer | 3 Critical · 4 High · 8 Medium · 9 Low · 5 runtime-verification items |
| Architecture | Software Architect | 3 Critical · 5 High · 6 Medium · 4 Low + Phase 2/3 extensibility analysis |
| UI/UX + Accessibility | Accessibility Auditor | 6 Blocker · 14 Serious · 9 Moderate · 3 Minor + measured contrast table |
| Usability & operations | UX Researcher | friction table · time-to-competence · error-recovery matrix · go-live assessment |
| Capacity & scale | Performance Benchmarker | **measured** on a 12.5M-row replica of the real schema · capacity table · 7-rank bottleneck list · growth model · 5-scenario load-test plan |

**Cross-confirmed findings** — surfaced independently by two or more reviewers, and therefore the
ones I'd trust most: money stored unrounded (architecture + security) · `order_events` mutable at
the DB (architecture + security) · missing hot-path indexes (architecture + capacity, the latter
with numbers) · the dead `waiter:` group (usability + architecture) · the stale `CLAUDE.md` #4
claim (accessibility + capacity) · the KDS 4-attempt reconnect cap (architecture + usability +
capacity).

*Report generated 2026-07-26. Update the status column as items land; add the commit SHA next to
each ☑ so the reasoning stays traceable.*
