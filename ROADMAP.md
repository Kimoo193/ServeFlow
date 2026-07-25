# ServeFlow — Hardening Roadmap

Working document for the post-review hardening programme. Written 2026-07-25.

The five items came out of a lead-engineering review of the Phase-1 codebase. Four are done and
on `main`; one remains. Each item below is written to be picked up cold — what the problem was,
why it mattered, and the decisions made so nobody re-litigates them.

---

## Status at a glance

| # | Item | State |
|---|---|---|
| 1 | Real authentication (login, session hardening) | **Done** — backend `d3b6aba`, frontend below |
| 2 | Authorize the order lifecycle | **Done** — `a11affc` |
| 3 | Combos: make them orderable | **Not started** ← **only item left** |
| 4 | Reconcile `CLAUDE.md` with reality | **Done** — `32b5e7f` |
| 5 | A thin frontend test layer | **Done** — `316d8e5`, `2411526` |

Tests: **105 backend** (was 80 at review time) and **84 frontend** (was 0). All gates green;
all three Angular apps build clean.

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

### ✅ 4 — `CLAUDE.md` reconciled with reality (`32b5e7f`)

The doc promised Clean Architecture with invariants in Domain and use-cases in Application.
Measured: Domain 344 lines of property bags, Application 3 files / 33 lines, every rule inline
in `Pos.Api` endpoint lambdas. The transaction-script approach stays — retrofitting buys little
— but the doc now says so, and says the thing that matters: **read the endpoint, not
`Pos.Domain`**, and promote a rule to the entity when it appears at several sites.

`Order.IsAcceptingChanges` (+ the static `AcceptingChanges` expression for EF, which cannot
translate the computed property) replaced the Open-or-InProgress guard at its four call sites.
Verified no schema impact: `ef migrations has-pending-model-changes` reports none.

Open decisions #1, #3 and #4 closed against the code rather than the notes — including that
**KimooKit never existed**, and the gap where bump events reach only the `waiter:` group, which
the frontend model comment had claimed otherwise.

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

### ✅ 5 — A frontend test layer (`316d8e5`, `2411526`)

From **zero** `.spec.ts` files to **84 tests**. No packages were needed: `vitest` 4 + `jsdom`
and the `@angular/build:unit-test` builder were already configured on all four projects — this
roadmap's earlier note about needing a runner and rule-7 approval was wrong.

- **shared/auth (28)** — login request shape; `userId`/`role` taken from the response, not the
  request; password never persisted; `APP_ID`-scoped storage key and two apps not sharing a
  session; restore accepting a future `exp` and rejecting past/non-JWT/garbled/context-less/
  malformed values; a login→refresh round trip; both interceptors, including that a 401 from
  `/auth/login` neither clears the session nor redirects.
- **KDS station board (34)** — the event reducer. Notably `firstSeenAt` surviving a reload (so a
  reconnect doesn't reset every aging timer) and a re-delivered `OrderFired` merging by line id
  rather than doubling the lines.
- **The three connect screens (11 each)** — validation before submit, exact navigation target,
  and the password signal cleared after a successful login.

Every spec was checked by **mutation**, not just by passing: dropping the `/auth/login`
exemption fails exactly one test; reverting the storage key to the old shared constant fails 13.

This found a real bug, now fixed: `onStart`/`onReady` set `error()` and then called `reload()`,
which clears `error()` on success — so a failed bump reverted the line with nothing on screen
to say why. The message is now set after the reload, and a reload failure outranks it.

## Remaining

### 3 — Combos: make them orderable

**Combos are a dead end today**: full CRUD in the API and the Manager UI (36 references in
`MenuEndpoints.cs`, a whole `combo-panel` component) and **zero references in
`OrderEndpoints.cs`**. A manager can build a combo nobody can ever order, with no warning.

**Decision made (open decision #2 in `CLAUDE.md`): the bundle price wins.** A combo expands into
component `OrderLine`s per BR-O-06, with `Combo.Price` allocated across them proportionally to
list price so `subtotal` over non-voided lines equals the bundle price (BR-B-01 holds) and
per-item reporting stays meaningful.

**Precision decided 2026-07-26 by Kareem: (a), expand per unit.** `order_lines.unit_price` is
`numeric(10,2)`, and with a quantity > 1 on a single line no 2-decimal unit price can make
`Σ(quantity × unit_price)` equal an arbitrary bundle price:

```
Combo = 10.00, one component ×3
  one line, unit_price 3.33 ×3 =  9.99   ✗
  one line, unit_price 3.34 ×3 = 10.02   ✗
  three quantity-1 lines: 3.34 + 3.33 + 3.33 = 10.00   ✓
```

So each component **unit** becomes its own quantity-1 `OrderLine`, and the remainder cents are
distributed one per line until exhausted. Widening the column was rejected: `1/3` does not
terminate at any decimal precision, so it reduces the error without removing it, and it touches
every money path.

Two of the consequences resolve against the specs; a third is genuinely open.

- **Voiding one unit — resolved, no special handling.** BR-B-01 says the bill is computed from
  non-voided lines only, so a voided component simply takes its allocated share out of the
  subtotal. Voiding one unit of a three-unit combo drops roughly a third of the bundle price,
  which is the coherent answer. No combo-aware void logic is needed.
- **Grouping the lines on the KDS ticket — resolved, do not do it.** BR-O-06 says each component
  line "is routed to its own station independently", so a combo's lines may legitimately land on
  *different* station screens and cannot be one ticket. BR-O-04/05 ask only for sequence order
  and station routing. The earlier worry here was misplaced.
- **⚠️ Open: `OrderLine` has no combo provenance.** There is no `ComboId` column, so once a combo
  expands, nothing records that those lines came from one. Consequences: the receipt (BR-B-04)
  shows a Burger at `3.34` with no explanation of why it is not `8.00`, and a whole combo cannot
  be voided in one action. Nothing in the spec requires provenance, so it can ship without —
  but adding it later is a migration on a hot table. **Decide before building**, and note a
  nullable `combo_id` on `order_lines` needs a migration, which per CLAUDE.md must be shown and
  approved first.

The test must assert exactness — `Σ(qty × unit_price) == Combo.Price × comboQty` — with awkward
numbers (a price that does not divide evenly) and a component of quantity 3.

Until this ships, consider hiding the Combos tab: shipping configurable-but-unorderable is worse
than shipping neither.

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
