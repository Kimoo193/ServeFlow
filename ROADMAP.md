# ServeFlow — Hardening Roadmap

Working document for the post-review hardening programme. Written 2026-07-25.

The five items came out of a lead-engineering review of the Phase-1 codebase. Two are done and
on `main`; three remain. Each remaining item below is written to be picked up cold — what the
problem is, why it matters, and the decisions already made so nobody re-litigates them.

---

## Status at a glance

| # | Item | State |
|---|---|---|
| 1 | Real authentication (login, session hardening) | **Done** — backend `d3b6aba`, frontend below |
| 2 | Authorize the order lifecycle | **Done** — `a11affc` |
| 3 | Combos: decide pricing or hide the feature | **Not started** — decision made, unimplemented |
| 4 | Reconcile `CLAUDE.md` with reality | **Not started** |
| 5 | A thin frontend test layer | **Not started** ← **start here** |

Tests: **105 passing** (was 80 at review time). Backend gates green; all three Angular apps
build clean.

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

### ✅ 1b — Frontend auth hardening

All five defects fixed and verified in a live browser against the running stack.

- **`/auth/login` replaces `/dev/token`.** Connect screens take username + password; tenant
  and branch stay (the terminal is provisioned with them). `SessionService.connect` is now
  `login`, and `loginErrorMessage` maps 400/401/429/offline onto one shared wording.
- **Per-app storage keys** via an `APP_ID` injection token → `sf.{kds,cashier,manager}.session`.
  This was not hypothetical: on ports 4200 and 4201 the Manager and Cashier apps were each
  found storing their session under `sf.kds.session`.
- **The KDS authenticates as its signed-in user**, no hardcoded role. Verified live: a
  `Kitchen` token loads the board and bumps lines (200) but is refused staff, menu, and
  history (403) — all three of which succeeded under the old Manager token.
- **Expiry is enforced on restore** by decoding the JWT `exp`; anything expired or without a
  readable `exp` is discarded without a network round-trip.
- **`authErrorInterceptor` handles 401 globally** — clears the session and routes to connect,
  while deliberately ignoring 401s from `/auth/login` so a wrong password doesn't bounce the
  operator off the form.

Also fixed here, found only because the UI was driven for real: **`DevSeeder` seeded the
permission matrix only when the tenant had no grants at all**, so the five permissions added
in `a11affc` never reached any pre-existing dev database, and the KDS 403'd on every bump.
It now grants each missing `(role, permission)` pair. Two regression tests cover it, and
both were confirmed to fail against the old guard before being kept.

## Remaining

### 5 — A thin frontend test layer  ← **start here**

**Zero `.spec.ts` files across all four frontend projects**, despite `test` targets being
configured on each. ~4k lines of UI verified only by manual browser QA.

This moved to the front of the queue because item 1b just demonstrated the cost: the KDS
permission bug and the seeder bug were both found by driving a browser by hand, and neither
would survive a refactor undetected without tests. The auth layer is also now the
best-specified, most test-shaped code in the frontend.

Highest value for least effort:
- `SessionService`: restore with a valid token, with an expired one, with a non-JWT string;
  `login` persisting under the injected `APP_ID` key; `disconnect` clearing it.
- `authErrorInterceptor`: a 401 in-session clears and redirects; a 401 from `/auth/login`
  does neither.
- `loginErrorMessage`: each status maps to its wording.
- The three connect flows: validation before submit, and no password left in a signal after
  a successful login.
- The KDS board's event reducer (`OrderFired` / `OrderLineVoided` / status bumps).

Note `ng test` has never been run in this workspace — expect to install and wire a runner
before writing the first spec (needs approval per CLAUDE.md rule 7).

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
- Raw-SQL tests must wrap `RlsTestSql.SetTenantAsync` in a transaction. `SET LOCAL` outside
  one is silently a no-op, RLS then hides every row, and assertions pass vacuously — a
  `DELETE` "succeeds" having removed nothing. Assert on affected row counts to catch it.
