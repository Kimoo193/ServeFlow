# Phase 1 — Build Plan (Vertical Slices)

Build top-to-bottom, one slice at a time. Each slice must be runnable and tested before the
next. A slice is "done" only when its acceptance criteria pass. Do **not** pull work forward
from later slices.

---

## Slice 0 — Scaffold & infra

**Goal:** an empty but runnable, tested skeleton.

- [ ] `.NET 10` solution `backend/Pos.sln` with `Pos.Domain`, `Pos.Application`,
      `Pos.Infrastructure` (classlibs) and `Pos.Api` (web), wired per CLAUDE.md dependency
      direction.
- [ ] `Pos.Infrastructure`: EF Core + Npgsql, empty `PosDbContext`, DI extension.
- [ ] `Pos.Api`: composition root, `GET /health` checking Postgres + Redis.
- [ ] `docker-compose.yml`: PostgreSQL 16 + Redis 7 with volumes.
- [ ] Test projects `Pos.Domain.Tests`, `Pos.Application.Tests`.

**Acceptance:** `dotnet build` and `dotnet test` pass; `docker compose up` runs; `/health`
returns healthy with the containers up. No domain entities yet.

## Slice 1 — Identity & tenant/branch context (RLS foundation)

**Goal:** an authenticated request resolves a tenant + branch, and RLS is proven.

- [ ] `Tenant`, `Branch`, `BranchSettings` entities + EF config + migration.
- [ ] `ITenantContext` (Application) + Infrastructure impl reading the JWT claim.
- [ ] Integrate KimooKit auth; a login yields a JWT carrying tenant + role.
- [ ] Apply `FORCE ROW LEVEL SECURITY` + policies; app connects as non-superuser; tenant id
      set per request (`SET LOCAL app.tenant_id`).
- [ ] Seed one tenant + one branch + one Owner user.

**Acceptance:** an integration test proves tenant A **cannot** read tenant B's branch rows,
via RLS, even with a crafted query. `/health` still green.

## Slice 2 — Menu

**Goal:** a menu the tablet can read and an admin can manage.

- [ ] `Category`, `MenuItem` (with `station_id`, `availability` enum), `ModifierGroup`,
      `Modifier`, `ItemModifierGroup` (reusable), `Combo`, `ComboItem`, `Station`.
- [ ] Admin CRUD endpoints; read endpoint returning the menu tree for a branch.
- [ ] Availability enum enforced (Hidden not returned to tablet; OutOfStock returned but
      flagged).

**Acceptance:** menu tree endpoint returns categories → items → modifier groups with correct
availability flags; unit tests cover availability filtering.

## Slice 3 — Dining area & order building

**Goal:** open a table and add draft lines.

- [ ] `Section`, `DiningTable` (status), `Order`, `OrderLine`, `OrderLineModifier`.
- [ ] Open order on a free table (enforce one-open-order-per-table constraint).
- [ ] Add/edit/remove **draft** lines with modifier + price **snapshots** and `sequence`.
- [ ] `row_version` on `Order`.

**Acceptance:** cannot open two orders on one table; draft lines snapshot price/modifiers;
concurrent edit with stale `row_version` is rejected. Tests cover both.

## Slice 4 — Fire batch, SignalR & KDS receive

**Goal:** fired lines reach the right station live.

- [ ] Fire endpoint: assign one `fire_batch_id`, move lines Draft→Fired, write
      `OrderEvent(Fired)`, broadcast per station over SignalR (Redis backplane).
- [ ] Minimal KDS read: ticket = lines grouped by `(order, station, fire_batch)`, ordered by
      `sequence`.

**Acceptance:** firing a mixed order routes each line to its station's group; a second fire
creates a new batch without touching the first; broadcast latency acceptable locally.

## Slice 5 — Per-line status round-trip

**Goal:** kitchen actions update the tablet live.

- [ ] KDS actions: Fired→Preparing→Ready (+ Served by waiter), each writing an `OrderEvent`.
- [ ] Broadcast status changes back to the order's waiter group.

**Acceptance:** bumping a line on KDS updates the waiter view without refresh; invalid
transitions rejected.

## Slice 6 — Modify / void with audit & permissions

**Goal:** safe changes after firing.

- [ ] Modify/void as **new events** per the BR-M permission table (role-gated; reason
      required; waste flag when Preparing).
- [ ] KDS alerts: NEW / VOID / CHANGE.
- [ ] `OrderEvent` append-only with `order_line_id` (nullable), `details` (jsonb before→after),
      `reason`, `performed_by`, `performed_at`.

**Acceptance:** a void-while-Preparing requires Manager + reason and records a waste-flagged,
immutable event; the audit trail can answer "who voided what, when, from→to". Tests cover the
permission matrix.

## Slice 7 — Close & receipt

**Goal:** close the order and print.

- [ ] Compute subtotal/service/tax/total from `BranchSettings`; **snapshot** onto `Order` at
      Completed; release the table.
- [ ] Block close while any line is Fired/Preparing.
- [ ] Receipt render + ESC/POS network print.

**Acceptance:** totals match BR-B math; a later `BranchSettings` rate change does not alter a
completed order; close is blocked with unfinished lines.

## Slice 8 — Availability realtime & offline hardening

**Goal:** production-grade edges.

- [ ] Availability changes broadcast live to tablets; OutOfStock/Hidden enforced server-side.
- [ ] Tablet offline queue + conflict-safe sync (no lost/duplicated lines on reconnect).

**Acceptance:** an item 86'd on admin becomes non-orderable on the tablet in real time;
offline-created orders sync exactly once on reconnect.

---

### Frontend (introduced alongside the slices that need them)

- Waiter (Flutter): from Slice 3; wire live status at Slice 5. Wireframe in
  `waiter-app-wireframe.html`.
- KDS (React): from Slice 4.
- Cashier (React): from Slice 7.
- Admin (React): minimal, from Slice 2.
