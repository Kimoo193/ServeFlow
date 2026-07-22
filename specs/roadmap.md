# Post-Phase-1 Roadmap

> Nothing here is built until all 8 Phase 1 slices are complete and passing.

---

## Guiding rule

Design for extension, build only when scheduled. Every item below is additive: it must
attach to a seam that already exists (or will exist) after Phase 1 — an entity, field, or
endpoint — rather than requiring a redesign of any Phase 1 decision. If an item can't name
its seam, it isn't ready to be scheduled.

---

## Phase 2 — Table & billing operations

### Payments
- **What**: Record one or more payments (cash, card, wallet) against a completed/closing order.
- **Attaches to seam**: `Order.status = Completed` today has no payment record (BR-B-06 /
  specs/02 §2 "Billing" — "No payment capture yet"). Phase 2 adds a `Payment` entity
  alongside `Order`, not replacing the close flow. `Order` already carries snapshotted
  `total` to reconcile against.
- **Does NOT change**: Close still means "receipt is correct" (BR-B-03 snapshot rule) —
  payment capture layers on top of close, it doesn't redefine what `Completed` means, and it
  must not touch the append-only `OrderEvent` model except by adding new event types.
- **Open questions**: none named in specs beyond "no payment capture in Phase 1" (specs/02
  §2, CLAUDE.md domain invariants) — the Payment entity's own field shape is undesigned.

### Split Bill
- **What**: Divide one order's total across multiple payments/receipts (e.g., by seat or by
  amount).
- **Attaches to seam**: `OrderLine`'s independence from a single bill total, plus the future
  `Payment` entity above — a split is multiple `Payment` rows (or a future `Check` grouping)
  against one `Order.total`.
- **Does NOT change**: Must not change how `OrderLine` snapshots price/modifiers at fire
  time, and must not alter BR-B totals math (subtotal → service → tax → total) computed from
  non-voided lines.
- **Open questions**: whether splitting is modeled as sub-totals on existing `OrderLine`s or
  needs a new `Check`/`BillPortion` entity — not decided in specs/02.

### Move / Merge Table
- **What**: Move an open order to a different table, or merge two tables' orders into one.
- **Attaches to seam**: `DiningTable`/`Order`'s existing `table_id` relationship
  (specs/02 §2 Ordering: `Order.table_id`, nullable → Takeaway).
- **Does NOT change**: Must not weaken the one-open-order-per-table constraint (BR-C-03 — at
  most one non-closed order per `DiningTable`, DB unique constraint) without an explicit new
  merge/move state; must not retroactively rewrite `OrderLine.sequence` or fired-line history.
- **Open questions**: how a merge reconciles two `waiter_id` values and two sets of
  `fire_batch_id`s onto one order — not addressed in specs/02–04.

### Reservations
- **What**: Future table bookings ahead of walk-in seating.
- **Attaches to seam**: `DiningTable.status` (Available / Occupied today, specs/02 §2 Dining
  Area) — reservations add a new status/window rather than replacing the existing enum.
- **Does NOT change**: Must not interfere with BR-C-03 (one non-closed order per table) or
  require `Order` to exist before a table is physically occupied.
- **Open questions**: entirely undesigned in specs — only named as a Phase 2 bounded context
  in specs/02 §1 ("Reservations | P2 | Future table bookings").

### Discounts
- **What**: A discount/promotion engine applied to an order or line.
- **Attaches to seam**: `Order.discount_amount`, which already exists as a snapshotted field
  in Phase 1 (specs/02 §2 Ordering; BR-B computation table's "Total = subtotal + service +
  tax − discount"). Phase 1 ships the *field*; Phase 2 ships the *engine* that populates it
  with real rules instead of a constant.
- **Does NOT change**: Must not change BR-B-03 (totals snapshotted at close) or BR-B-01
  (computed from non-voided lines only) — a discount is one more input to the existing
  formula, not a new computation pipeline.
- **Open questions**: rule types (percentage, fixed, BOGO, promo codes) are undesigned;
  CLAUDE.md golden rule #2 names "discounts engine" only as deferred scope, no shape given.

### Customer Accounts
- **What**: Persistent customer identity across visits (contact info, order history).
- **Attaches to seam**: `Order.customer_name` (nullable, Takeaway-only today, specs/02 §2)
  is the closest existing seam — a `Customer` entity would let `customer_name` become a
  lookup instead of free text, additively.
- **Does NOT change**: Must not require every `Order` to have a customer (dine-in orders
  today have none), and must not turn `Order` into a customer-owned aggregate — `waiter_id`
  and `table_id` remain the primary relations.
- **Open questions**: whether Customer Accounts is scoped to loyalty (Phase 3, see below) or
  is a separate, earlier Phase 2 concern — specs/01 §6 lists "customer accounts" under
  Phase 2 while specs/02 §1 lists "Loyalty" as a distinct P3 context; the boundary between
  the two isn't drawn.

---

## Phase 2.5 — QR / Customer-facing (staged)

### Stage A — QR menu (read-only)
- Owner prints a per-table QR code; a customer scans it and browses the branch's menu
  (prices, modifiers, availability) with no login. No writes, no ordering, no cart.
- **Attaches to**: the menu read path built in Slice 2 (the existing "menu tree for a
  branch" endpoint), plus a new per-table QR token/link.
- This is the **first public/anonymous read path** in the system. It must not bypass RLS:
  access is scoped by a branch/table-specific token passed in the request; tenant isolation
  itself is never relaxed — the token resolves to a tenant/branch the same way a JWT does
  today, it does not grant a backdoor around `ITenantContext`/RLS.
- **Prerequisite**: Slice 2 complete.

### Stage B — Customer self-ordering
**DEFERRED** — blocked on open questions, none resolved here:
1. Does a customer-submitted order go straight to the kitchen, or does it require
   waiter/cashier confirmation first?
2. How is the anonymous customer/session identified, and how is abuse (spam orders)
   prevented?
3. How is a customer-submitted request modeled so it does **not** violate the existing
   model — an `Order` is opened by a `waiter_id`, is role-gated, and every action is audited
   (BR-C-04, BR-AU)? Most likely as a separate "pending request" entity that staff must
   confirm into a real `Order`, not a direct anonymous `Order`.

Confirmed (not open): payment stays cash-to-the-waiter in this stage — no online payment
capture is introduced by Stage B.

---

## Phase 3 — Inventory, loyalty & delivery execution

### Inventory
- **What**: Stock deduction per sale, low-stock alerts.
- **Attaches to seam**: `MenuItem.availability` (Available / OutOfStock / Hidden, specs/02
  §2 Menu, BR-A) is the seam — inventory depletion would drive `OutOfStock` transitions
  automatically instead of a manual admin toggle.
- **Does NOT change**: Must not change the three-state availability enum's *meaning* to the
  tablet (BR-A-01/02 — OutOfStock still shown greyed-out, not orderable; Hidden still
  invisible); inventory is a new trigger for an existing state, not a new state machine.
- **Open questions**: undesigned in specs beyond the bounded-context listing
  (specs/02 §1: "Inventory | P3 | Stock deduction, purchase orders").

### Purchase Orders
- **What**: Ordering stock from suppliers.
- **Attaches to seam**: Same Inventory bounded context (specs/02 §1); no Phase 1 entity to
  attach to yet — this is a net-new subsystem gated entirely behind Inventory landing first.
- **Does NOT change**: N/A — no Phase 1 invariant touches purchasing.
- **Open questions**: entirely undesigned; not detailed anywhere in specs/01 or 02 beyond the
  single bounded-context line.

### Loyalty
- **What**: Customer accounts, points accrual/redemption.
- **Attaches to seam**: specs/02 §1 lists Loyalty as its own P3 bounded context ("Customer
  accounts, points"), separate from the Phase 2 "customer accounts" mention in specs/01 §6.
  Whichever Customer entity Phase 2 introduces (see above) is the seam Loyalty extends with
  a points ledger.
- **Does NOT change**: Must not require a customer identity on every order (dine-in remains
  anonymous by default today).
- **Open questions**: the Phase 2/Phase 3 boundary for "customer accounts" itself is unclear
  (see Customer Accounts entry above) — Loyalty inherits that same ambiguity.

### Delivery / Drive-thru execution
- **What**: Operational execution of delivery and drive-thru order channels (dispatch,
  courier assignment, drive-thru lane flow).
- **Attaches to seam**: `Order.order_type` enum, explicitly designed for this
  (specs/02 §3 key decision 7: "`OrderType` from day one. Enum `{DineIn, Takeaway}` in P1,
  extensible to `{Delivery, DriveThru}`. `table_id` is nullable so non-dine-in channels reuse
  the same `Order`."). This is a named, intentional Phase 1 seam.
- **Does NOT change**: Must not require `table_id` to be non-null, and must not alter how
  `OrderLine` fire/status lifecycle works — new `order_type` values reuse the existing
  `Order`/`OrderLine` machinery, they don't fork it.
- **Open questions**: courier/dispatch data model, and whether "delivery dispatch platform"
  work (explicitly a non-goal per specs/01 §8) is in scope at all versus only the
  `order_type` value and in-house execution — specs/01 says this product is "not... a
  delivery dispatch platform," so Phase 3 delivery scope may be limited to marking/tracking
  an order as `Delivery`, not integrating third-party dispatch.

### QR ordering
- Listed under Phase 3 in specs/01 §6's high-level roadmap line, but detailed and superseded
  by the explicit Phase 2.5 staged design above (Stage A shippable earlier, Stage B
  deferred). See Phase 2.5 section — not repeated here to avoid two conflicting plans.

### Analytics / Reporting
- **What**: Cross-order reporting, sales dashboards, trend analysis.
- **Attaches to seam**: `OrderEvent` (append-only audit log, BR-AU) is the natural source of
  truth for historical analytics — every state change is already fully reconstructable
  (NFR-4), so reporting is a read-side projection over existing data, not a new write path.
- **Does NOT change**: Must never write to `OrderEvent`, and must not require any change to
  its append-only/immutable contract (BR-AU-01).
- **Open questions**: undesigned; specs/01 §6 names "analytics" only at the Phase 3
  roadmap-line level with no further detail.

### Kitchen performance metrics
- **What**: Timing analytics per station (time-to-Preparing, time-to-Ready, etc.).
- **Attaches to seam**: Same `OrderEvent` audit trail, keyed by `order_line_id` and
  `performed_at` — station timing is derivable from existing Fired/Preparing/Ready event
  timestamps (specs/02 §2 OrderEvent; specs/04 BR-AU-02).
- **Does NOT change**: Read-only over `OrderEvent`; does not introduce a new mutable
  aggregate.
- **Open questions**: not named explicitly in specs/01 or 02 as its own line item — grouped
  here under the general "Analytics" Phase 3 roadmap entry (specs/01 §6) since KDS timing is
  the most direct analytics use of data Phase 1 already produces.

---

## Cross-cutting deferred

- **True multi-branch operations** — the schema is already branch-ready: `branch_id` (and
  `tenant_id`) is carried on every row from day one (specs/02 §3 key decision 6; CLAUDE.md
  multi-tenancy section), and specs/02 explicitly notes `Branch`/`Station` are "one active
  branch in P1; column present for the future." Multi-branch is therefore a **workflow/UI**
  feature (branch switcher, cross-branch reporting, per-branch staff assignment) layered on
  an existing column — not a schema change.

---

## Explicit non-goals

Restated from specs/01-vision.md §8, near-verbatim:

- This is **not** a payment processor.
- This is **not** an accounting system.
- This is **not** a delivery dispatch platform.

Those integrate in later phases; treating them as MVP scope would sink the MVP.

Additionally, per specs/01 §5 "Deferred (not in Phase 1)": payments, split/merge/move table,
reservations, inventory, discount/promotion engine, delivery/drive-thru execution, QR
customer ordering, and loyalty are all explicitly out of Phase 1 scope — this entire
document exists to place each of them, without redesigning anything Phase 1 already decided.
