# CLAUDE.md — Restaurant / Cafe POS

Project memory and rules for Claude Code. Read this fully before touching any task.

---

## What this is

A multi-tenant POS where a **waiter** takes orders on a tablet, the **kitchen** sees them
live on a display (KDS), and the **cashier** closes the bill and prints a receipt. One
codebase serves restaurants, cafes, and cloud kitchens. We are building **Phase 1 (MVP)**.

Full specs live in `specs/` (Architecture, Vision, Domain Model, State Machines, Business
Rules, Screen Flows, API Contracts, Tasks). When a task conflicts with those docs, the docs
win — flag the conflict.

## Golden rules (read first)

1. **Vertical slices, one at a time.** Build one feature end-to-end (API → DB → test), keep
   the system runnable at every commit. Do **not** build broad horizontal layers.
2. **Design for extension, don't build the extension.** Do **not** implement any Phase 2/3
   feature (payments, split/merge/move table, reservations, inventory, delivery, loyalty,
   discounts engine) unless a task explicitly asks for it.
3. **Tenant isolation is enforced at the database (RLS), never by `WHERE` clauses alone.**
   Never write code that bypasses RLS. No superuser DB access in app code, background jobs,
   or tests.
4. **`OrderEvent` is append-only and immutable.** Never emit an UPDATE or DELETE against it.
5. **Never mutate a fired `OrderLine`'s content in place.** Modifications after firing are
   **new events** that also raise a KDS alert.
6. **Snapshots, not live references.** Line prices, modifier names/prices, and order totals
   are captured at order/close time — never recomputed from current menu FKs.
7. **Ask before adding a NuGet/npm package.** Propose it, pin the version, wait for approval.

## Tech stack

- **Backend:** .NET 10, ASP.NET Core, Clean Architecture, EF Core, PostgreSQL 16 (RLS),
  Redis, SignalR, xUnit.
- **Auth:** KimooKit (self-hosted auth library — JWT, roles/permissions, tenant membership).
- **Frontend (later slices):** Flutter (waiter tablet); React 19 + Vite + TypeScript
  (KDS, cashier, admin).
- **Infra:** Docker Compose (Postgres + Redis) for local; Coolify as deploy target.

## Solution layout

```
restaurant-pos/
├── CLAUDE.md
├── docker-compose.yml
├── specs/                       # the design docs (00-architecture … 06-api-contracts, tasks)
└── backend/
    ├── Pos.sln
    ├── src/
    │   ├── Pos.Domain/          # entities, value objects, enums, invariants, domain events
    │   ├── Pos.Application/     # use-cases (commands/queries), interfaces (ITenantContext…)
    │   ├── Pos.Infrastructure/  # EF DbContext, migrations, RLS, Redis, SignalR impl
    │   └── Pos.Api/             # controllers, hubs, DI composition, auth middleware
    └── tests/
        ├── Pos.Domain.Tests/
        └── Pos.Application.Tests/
```

## Architecture rules

- **Dependency direction:** `Api → Application → Domain`; `Infrastructure → Application →
  Domain`; `Api → Infrastructure` (DI wiring only). **Domain has zero external dependencies.**
- **Domain** contains no EF attributes — mapping is Fluent config in Infrastructure.
- **Application** holds use-cases and interfaces only; no infrastructure concerns, no EF, no
  HTTP.
- **Infrastructure** owns `PosDbContext`, migrations, RLS policy SQL, Redis, and the SignalR
  hub implementation.
- **Api** owns endpoints, hub routes, middleware, and composition root.

## Multi-tenancy (critical)

- Every domain row carries `tenant_id` (and `branch_id` where applicable).
- `ITenantContext` (defined in Application) resolves the current tenant from the JWT.
- Isolation is enforced by **both** EF global query filters **and** PostgreSQL RLS.
- RLS policies use `FORCE ROW LEVEL SECURITY` (applies even to the table owner).
- The app connects as a **non-superuser** role; the tenant id is passed to Postgres per
  request/session (e.g. `SET LOCAL app.tenant_id`) and RLS policies read it.
- Background jobs and integration tests **run inside a tenant context**, never as superuser.

## Domain invariants (enforce these — see specs/04-business-rules.md)

- A `DiningTable` has **at most one** non-closed order (DB unique constraint). *(BR-C-03)*
- Firing sends **only the new fire batch** to the kitchen; earlier batches are untouched.
  Never re-send a whole order. *(BR-O-03)*
- KDS "ticket" = a **view** grouping `OrderLine` by `(order_id, station_id, fire_batch_id)`.
  There is **no `KitchenTicket` table** in Phase 1. *(domain decision)*
- `OrderLine.status` drives the lifecycle
  (Draft → Fired → Preparing → Ready → Served, + Voided). Order status is partly derived
  from its lines. *(specs/03)*
- Voiding a `Preparing` line requires a reason + Manager permission and sets a waste flag in
  the `OrderEvent` details. *(BR-M-03)*
- Every write to an order carries `row_version` (optimistic concurrency); stale writes are
  rejected, never silently overwritten. *(BR-C-02)*
- `MenuItem.availability ∈ { Available, OutOfStock, Hidden }`; OutOfStock/Hidden items are
  not orderable. *(BR-A)*
- Bill is computed from non-voided lines; tax/service come from `BranchSettings`; totals are
  **snapshotted onto the order at close**. *(BR-B)*
- No payment capture in Phase 1 — "close" = `Completed` + receipt, not a payment record.

## Conventions

- PKs are `uuid` (Guid). Money is `decimal`; currency from `BranchSettings`. Time is UTC via
  an `IClock` abstraction.
- One EF migration per slice; show it and wait for approval before applying.
- Every slice ships with tests: xUnit unit tests for domain rules, integration tests for RLS
  isolation and endpoints. **An RLS test that proves tenant A cannot read tenant B is
  mandatory once multi-tenancy lands.**
- Async methods end with `Async`. One public type per file. Nullable reference types on.

## What NOT to do

- Do not add any deferred (Phase 2/3) feature or its tables.
- Do not create a `KitchenTicket` table — use `OrderLine.fire_batch_id`.
- Do not mutate fired `OrderLine` content or any `OrderEvent` row.
- Do not call network/external services from Domain or Application.
- Do not bypass RLS or connect to Postgres as a superuser from app code.
- Do not add packages without approval.

## Open decisions (resolve before the noted slice)

These are genuinely undecided — do **not** invent answers; confirm with Kareem first.

1. **KimooKit contract** *(blocks Slice 1)* — exact JWT claim names for tenant/branch/role,
   and whether User/Role/permission rows live in KimooKit's tables or the Pos schema.
2. **Combo pricing** *(blocks Slice 2–3)* — a combo expands into component `OrderLine`s
   (BR-O-06) each with a snapshot `unit_price`, but `Combo.price` is a single bundle price.
   How the bundle price reconciles with the line snapshots for `subtotal` (BR-B-01) is open.
3. **Offline idempotency + `row_version` granularity** *(shapes Slice 3 schema)* — the
   exactly-once mechanism on reconnect (NFR-2). Recommended default (pending confirmation):
   client-generated `OrderLine` Guids as idempotency keys; keep `Order.row_version` for
   structural writes (add/fire/close/cancel) so KDS line-status bumps don't false-conflict.
4. **SignalR groups & hub auth** *(blocks Slice 4)* — how clients authenticate to `/posHub`
   and join station vs order/waiter groups.

## Working agreement

Kareem drives Claude Code in the terminal and reviews every change as the architect.
Prefer small, reviewable diffs. At the end of a task, summarize what changed, list any new
packages, and show the migration SQL. If a task is ambiguous or fights a spec, stop and ask.
