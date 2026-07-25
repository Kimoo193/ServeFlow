# ServeFlow — Hardening Roadmap

Working document for the post-review hardening programme. Written 2026-07-25.

The five items came out of a lead-engineering review of the Phase-1 codebase. Two are done and
on `main`; three remain. Each remaining item below is written to be picked up cold — what the
problem is, why it matters, and the decisions already made so nobody re-litigates them.

---

## Status at a glance

| # | Item | State |
|---|---|---|
| 1 | Real authentication (login, session hardening) | **Backend done** · frontend outstanding |
| 2 | Authorize the order lifecycle | **Done** — `a11affc` |
| 3 | Combos: decide pricing or hide the feature | **Not started** — decision made, unimplemented |
| 4 | Reconcile `CLAUDE.md` with reality | **Not started** |
| 5 | A thin frontend test layer | **Not started** |

Tests: **103 passing** (was 80 at review time). Backend gates green.

---

## Done

### ✅ 2 — Order lifecycle authorization (`a11affc`)

`Permission` gained `TakeOrder`, `FireOrder`, `BumpKitchenLine`, `ServeLine`, `ReprintReceipt`,
and 10 endpoints are now gated. Post-change audit of all 46 mutating endpoints: 42 filter-gated,
2 inline (`void`, `increase-quantity` — they carry the BR-M-03 own-waiter exception a blanket
filter can't express), 2 anonymous by design (`/auth/login`, `/dev/token`).

No migration was needed — `RolePermission` stores the enum as a string.

Default matrix seeded in `DevSeeder`: Manager all · Waiter take/fire/serve · Kitchen bump ·
Cashier close/history/reprint · Owner bypasses.

### ✅ 1a — First-party password login, backend (`d3b6aba`)

`POST /auth/login { tenantId, branchId, username, password }` returns the same JWT claim shape
`/dev/token` always did, so middleware and all three front-ends were unaffected.

- **KimooKit does not exist** — no package, no source, no private feed. It is referenced only in
  comments and docs. This endpoint is shaped so KimooKit or an OIDC provider can replace it
  behind the identical contract later.
- PBKDF2-SHA256, 210k iterations, 16-byte salt, `FixedTimeEquals`, BCL only (no new package).
- The user lookup runs inside a tenant-scoped transaction so **RLS** constrains it — login
  cannot enumerate users across tenants.
- Missing user / missing hash / inactive / wrong password all return one identical 401, with a
  dummy-hash verify when no user is found so timing doesn't leak username existence.
- Credentials are judged **before** the branch id, so an unauthenticated caller can't use the
  endpoint as an oracle for which branches exist. Regression test covers it.
- Rate limited 10/min **partitioned by client IP**, rejecting with 429.

Migration `AuthPasswordLogin` is **applied** to the dev DB (verified: both columns nullable, the
unique index present, all 8 pre-existing rows intact, login round-tripped end to end).

Dev credentials: `owner` / `manager` / `cashier` / `waiter` / `kitchen`, password `devpass123`.

---

## Remaining

### 1b — Frontend auth hardening  ← **start here**

The backend now has a real login, but **the front-ends still authenticate by POSTing to
`/dev/token`**, which only exists in Development. Until this lands, no app can talk to a
production API. This is the release blocker.

Five separate defects, all in `frontend/projects/shared/src/lib/auth/`:

1. **Point the apps at `/auth/login`.** `session.service.ts:46` calls `/dev/token`. Replace with
   the real endpoint; the connect screens become username + password instead of a `userId` GUID
   (keep the tenant/branch fields — the terminal is provisioned with them, and `/auth/login`
   expects them).
2. **Per-app storage keys.** `session.service.ts:18` hardcodes `STORAGE_KEY = 'sf.kds.session'`
   and *all three apps share it*. Distinct dev ports are distinct origins, which is the only
   reason this has never bitten — deploy Manager/Cashier/KDS on one host and they silently
   overwrite each other's sessions. Make the key per-app (inject it, or derive from an app id).
3. **The KDS must stop authenticating as `Manager`.** `kds/src/app/station-connect.ts:50`
   requests `role: 'Manager'`, so a physically accessible kitchen wall screen holds management
   rights. It should log in as a `Kitchen` user; that role now has exactly `BumpKitchenLine`.
4. **Handle expiry on restore.** `session.service.ts:62` trusts any persisted token. Tokens last
   8h; decode `exp` and treat an expired token as no session.
5. **Handle 401 globally.** `auth.interceptor.ts` only attaches the token — there is no 401
   handling anywhere in the frontend. Add an interceptor that clears the session and routes to
   connect. **Why this matters most:** a KDS runs unattended for a full shift, so today the
   board silently starts erroring mid-service with no route back. That is the most likely
   real-world outage in the current design.

Gate: `npx ng build manager|cashier|kds` clean, plus a manual login round-trip per app.

### 3 — Combos: make them orderable

**Combos are a dead end today**: full CRUD in the API and the Manager UI (36 references in
`MenuEndpoints.cs`, a whole `combo-panel` component) and **zero references in
`OrderEndpoints.cs`**. A manager can build a combo nobody can ever order, with no warning.

**Decision made (open decision #2 in `CLAUDE.md`): the bundle price wins.** A combo expands into
component `OrderLine`s per BR-O-06, with `Combo.Price` allocated across them proportionally to
list price so `subtotal` over non-voided lines equals the bundle price (BR-B-01 holds) and
per-item reporting stays meaningful.

⚠️ **Implementation constraint found while scoping:** `order_lines.unit_price` is
`numeric(10,2)`. With a 2-decimal unit price, `Σ(quantity × unit_price)` cannot generally be made
to equal an arbitrary bundle price when a component quantity is > 1. Options:

- **(a)** Expand per *unit* — one `OrderLine` of quantity 1 per component unit. Always exact,
  simple, and arguably better for KDS bump granularity. Costs more lines on the ticket.
- **(b)** Allocate line totals exactly and let the last line's unit price absorb the remainder —
  leaves a residual of up to (qty−1) cents on the money path. Not recommended.
- **(c)** Widen `unit_price` precision. A migration on the money column; broadest blast radius.

Recommendation: **(a)**. Whichever is chosen, the test must assert exactness:
`Σ(qty × unit_price) == Combo.Price × comboQty`, including a case with awkward numbers and a
quantity-2 component.

Also: until this ships, consider hiding the Combos tab — shipping configurable-but-unorderable is
worse than shipping neither.

### 4 — Reconcile `CLAUDE.md` with reality

`CLAUDE.md` describes Clean Architecture with invariants in Domain and use-cases in Application.
Measured reality:

```
Pos.Domain           31 files    350 lines   anemic: property bags + enums, no behaviour
Pos.Application       6 files     67 lines   3 real files: IClock, ITenantContext, 1 exception
Pos.Infrastructure   45 files   8318 lines
Pos.Api              16 files   2395 lines   OrderEndpoints.cs alone is 1239 lines
```

There are **no use-cases**; all logic is transaction-script style in endpoint lambdas over EF.
That is a defensible MVP choice — the risk is the mismatch: the doc sends contributors (and
agents) to Domain for invariants, they find nothing, and re-implement a rule inline. The
`status != Open && status != InProgress` guard is already duplicated at four sites
(`OrderEndpoints.cs:144, 203, 447` and the `.Where` at `:277`).

Do **not** retrofit Clean Architecture — little gain. Instead:

- Make the doc state what the code actually is.
- Extract the repeated order-status guards onto `Order` as real methods — the value of a domain
  layer without the ceremony.
- Record the decisions already made in code but still listed as "open":
  - #2 combo pricing — decided (above), not yet implemented.
  - #3 `row_version` granularity — shipped: `Order.RowVersion` only, for structural writes.
  - #4 SignalR groups / hub auth — shipped: `PosHub` verifies tenant ownership under RLS before
    joining a station/branch group.
- Note that `Permission` enforcement (deferred under #1) is now largely complete.

### 5 — A thin frontend test layer

**Zero `.spec.ts` files across all four frontend projects**, despite `test` targets being
configured on each. ~4k lines of UI verified only by manual browser QA. The `categoryId=itemId`
bug found earlier is exactly the class a single component test would have pinned.

Highest value for least effort:
- the three connect/login flows (once 1b lands),
- `SessionService` restore/expiry/clear logic,
- the KDS board's event reducer (`OrderFired` / `OrderLineVoided` / status bumps).

---

## Environment gotchas (cost real debugging time — don't rediscover)

- **`dotnet test` needs Redis running.** Testcontainers provides Postgres, but the fixtures point
  the SignalR backplane at `localhost:6389`, so `docker compose up -d redis` is required. Without
  it ~a third of the suite fails with 500s that look nothing like a Redis fault. (`README.md` used
  to claim compose wasn't needed at all; fixed in `d3b6aba`.)
- **Migrations must be applied as `pos_owner`**, not `pos_app` — RLS DDL needs the owner. The app
  itself connects as the non-superuser `pos_app` by design.
- The API does **not** auto-apply migrations; run `dotnet ef database update` explicitly.
- Delegating to OpenCode: use `deepseek/deepseek-v4-pro` or `-v4-flash`.
  `deepseek/deepseek-reasoner` is still listed by `opencode models` but the API rejects it.
