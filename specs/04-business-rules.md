# Business Rules (Phase 1)

> These rules govern behavior that **does not appear in the ERD**. In POS systems, most
> real bugs live here, not in the tables. Every rule has an ID for traceability, uses
> **shall** (mandatory) / **should** (recommended), and references states from the
> State Machines document.

---

## A. Ordering & firing (BR-O)

- **BR-O-01** — A waiter adds lines to an order as `Draft`. Draft lines are **not** visible
  to the kitchen.
- **BR-O-02** — Lines are sent to the kitchen only when the waiter **fires** them. Firing
  assigns one `fire_batch_id` to the batch and moves those lines to `Fired`.
- **BR-O-03** — When new lines are added after an earlier fire, firing them creates a
  **new fire batch**. The system **shall never re-send the whole order** — only the new
  batch is transmitted to the kitchen. *(Answers: "only new additions, or re-send all?")*
- **BR-O-04** — Each line carries a `sequence` within its order; the KDS **shall** display
  lines in `sequence` order so the kitchen sees them as the waiter took them.
- **BR-O-05** — Each line is routed to the KDS station of its `MenuItem.station_id`. A
  station screen **shall** show only lines routed to it.
- **BR-O-06** — A combo expands into its component lines at order time; each component line
  is routed to its own station independently.

## B. Modification & void (BR-M)

Modification permission depends on the **line's current state**. This is the table that
governs it:

| Line state | Edit qty / modifiers | Void / cancel | Kitchen notified? |
|---|---|---|---|
| **Draft** | Free — any staff on the order | Free, no reason | No (not fired yet) |
| **Fired** | New event; Waiter (own order) or Manager | Reason required; Waiter (own) or Manager | Yes — KDS alert |
| **Preparing** | New event; **Manager only** | Reason required; **Manager**; sets waste flag | Yes — KDS alert |
| **Ready** | No content edit | Void/return; **Manager** | Yes — KDS alert |
| **Served** | No content edit | Return; **Manager** | Yes — KDS alert |

- **BR-M-01** — A `Draft` line may be edited or removed freely, with no reason and no
  audit-critical event. *(Answers: "can an order be modified?" — yes, freely, before firing.)*
- **BR-M-02** — Once a line is `Fired` or later, it **shall not** be edited in place.
  Any change is recorded as a **new `OrderEvent`** and, where it affects the kitchen,
  raises a KDS alert. The original fired content is never silently mutated.
  *(Answers: "can an order be modified after prep starts?" — only via a new, audited,
  kitchen-notified event, and only by a permitted role.)*
- **BR-M-03** — Voiding a `Preparing` line **shall** require a Manager, a reason, and
  **shall** set a waste flag in the `OrderEvent.details`. The line disappears from active
  work but its history and the waste record remain. *(Answers: "what happens if an item is
  voided while Preparing?")*
- **BR-M-04** — A modification that reaches a fired line **shall** surface on the relevant
  KDS station as a distinct alert (`NEW ITEM`, `VOID`, or `CHANGE`) so the kitchen never
  sees an item silently appear or vanish mid-preparation.
- **BR-M-05** — Quantity increase on a fired line is treated as a **new** line/event in a
  new fire batch, not an in-place mutation, so the kitchen gets a clean "+N" instruction.

## C. Concurrency & multi-waiter (BR-C)

- **BR-C-01** — Any waiter **may** add lines to any `Open` order on any table; tables are a
  shared resource, not owned by one waiter. *(Answers: "can any waiter edit any table?" —
  yes for adding; role-gated for void/discount per BR-M.)*
- **BR-C-02** — Every write to an order **shall** carry the order's `row_version`
  (optimistic concurrency). A write with a stale version **shall** be rejected and the
  client **shall** refresh — a change is **never** silently overwritten (NFR-5).
- **BR-C-03** — A `DiningTable` **shall** have at most one non-closed order at a time,
  enforced by a database unique constraint. Two waiters opening the same free table in the
  same instant → the second is rejected and shown the now-occupied table.
- **BR-C-04** — Every order is stamped with `waiter_id`; every event with `performed_by`.
  The system always knows who did what.

## D. Menu availability (BR-A)

`MenuItem.availability` is a three-state enum:

| Value | Meaning | Behavior in POS |
|---|---|---|
| **Available** | Normal | Orderable. |
| **OutOfStock** | 86'd — exists but sold out today | Shown greyed-out; **shall not** be added to an order. |
| **Hidden** | Not offered right now (seasonal, retired) | Not shown in the menu at all. |

- **BR-A-01** — The tablet **shall** prevent adding an `OutOfStock` or `Hidden` item to an
  order.
- **BR-A-02** — Changing availability **shall** take effect on connected tablets in real
  time (SignalR), so a just-86'd item stops being orderable immediately.

## E. Billing, tax & service (BR-B)

Computation order, expressed as a case table:

| Step | Formula |
|---|---|
| Subtotal | Σ (line `unit_price` × `quantity` + Σ modifier `price_delta`) for all non-voided lines |
| Service | `service_applies` ? subtotal × `service_charge_rate` : 0 |
| Taxable base | `tax_on_service` ? (subtotal + service) : subtotal |
| Tax | `tax_applies` ? taxable_base × `tax_rate` : 0 |
| Total | subtotal + service + tax − discount |

- **BR-B-01** — The bill **shall** be computed from non-voided lines only.
- **BR-B-02** — Tax and service rates are read from `BranchSettings`, not hard-coded.
- **BR-B-03** — On `Order → Completed`, the computed `subtotal`, `service_amount`,
  `tax_amount`, `discount_amount`, and `total` **shall** be **snapshotted** onto the order.
  A later change to `BranchSettings` **shall not** alter a completed order's totals.
- **BR-B-04** — A receipt **shall** be printable at close, containing: header (business,
  branch, date/time, order no., table or Takeaway no., waiter), non-voided lines with
  quantity × price and modifiers, then subtotal / service / tax / total.
- **BR-B-05** — An order **shall not** be closed while any line is `Fired` or `Preparing`;
  all lines must be `Served` or `Voided` first.
- **BR-B-06** *(Phase 1 note)* — Payment capture is out of scope. "Close" marks the order
  `Completed` and prints the receipt; it does **not** record a payment. The `Payment`
  entity is a Phase 2 seam.

## F. Audit (BR-AU)

- **BR-AU-01** — `OrderEvent` is **append-only and immutable**: the system **shall never**
  UPDATE or DELETE an event row.
- **BR-AU-02** — Every state-changing action on an order or line (create, fire, modify,
  quantity change, void, refire, complete, cancel) **shall** write an `OrderEvent` with
  `performed_by`, `performed_at`, and — where content changed — a `details` (jsonb)
  before→after snapshot and a `reason` where one is required.
- **BR-AU-03** — The audit trail **shall** be sufficient to answer, without inference,
  "who did *X* to order *N* at time *T*, and what was the value before and after?"

## G. Tenant isolation (BR-T)

- **BR-T-01** — Every domain row carries `tenant_id` (and `branch_id` where applicable).
- **BR-T-02** — Isolation **shall** be enforced by PostgreSQL row-level security using the
  tenant from the authenticated context (`ITenantContext`), **not** by application `WHERE`
  clauses alone (NFR-3).
- **BR-T-03** — Background jobs and integration tests **shall** run under a tenant context,
  never as a superuser that bypasses RLS.

---

## Answers to the four framing questions (index)

| Question | Governed by |
|---|---|
| Can an order be modified after prep starts? | BR-M-02, BR-M table |
| What happens if an item is voided while Preparing? | BR-M-03 (Manager + reason + waste flag) |
| Can any waiter edit any table? | BR-C-01 (add: yes) + BR-M (void/discount: role-gated) |
| Only new additions to the kitchen, or re-send all? | BR-O-03 (only the new fire batch) |
