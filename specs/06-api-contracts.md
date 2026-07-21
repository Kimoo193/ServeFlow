# API Contracts (Phase 1)

> This document defines the external contract between frontend and backend.
> APIs are derived from Screen Flows.
>
> **Base path:** every endpoint below is served under the version prefix **`/api/v1`**.
> The prefix is written out on each route so routes and the Versioning section agree.

---

# API Principles

REST

JSON

JWT Authentication

Optimistic Concurrency — every write carries `rowVersion` (body or `If-Match`)

RFC7807 Problem Details

---

# Authentication

POST /api/v1/auth/login

Response

```json
{
  "accessToken": "...",
  "expiresAt": "...",
  "tenantId": "...",
  "branchId": "...",
  "role": "Waiter"
}
```

> JWT issuance, roles, and tenant membership are provided by KimooKit. The exact claim
> names for tenant/branch/role are owned by KimooKit — see "Open decisions" in CLAUDE.md.

---

# Tables

GET /api/v1/tables

Returns

```json
[
  {
    "id": "...",
    "label": "12",
    "status": "Occupied"
  }
]
```

> `label` is the `DiningTable.label` string from the domain model (not a numeric `number`).

---

# Open Order — Dine-in

POST /api/v1/tables/{tableId}/orders

Response

```json
{
  "orderId": "...",
  "status": "Open",
  "rowVersion": 1
}
```

# Open Order — Takeaway

POST /api/v1/orders

Request

```json
{
  "orderType": "Takeaway",
  "customerName": "..."
}
```

Response

```json
{
  "orderId": "...",
  "status": "Open",
  "dailyNo": 42,
  "rowVersion": 1
}
```

> Takeaway has no table (`table_id` null); it captures `customerName` and returns the
> assigned `dailyNo` (screen flow §3, domain `Order.daily_no` / `customer_name`).

---

# Menu

GET /api/v1/menu

Returns

Categories

Items

Modifier Groups

Availability

> `Hidden` items are **not** returned to the tablet/waiter menu; `OutOfStock` items are
> returned but flagged (greyed-out, not orderable). The Manager admin menu endpoints return
> all items including `Hidden`. (Slice 2, BR-A.)

---

# Add Draft Line

POST /api/v1/orders/{orderId}/lines

Request

```json
{
  "menuItemId": "...",
  "quantity": 2,
  "modifiers": ["<modifierId>"],
  "rowVersion": 3
}
```

Response

```json
{
  "lineId": "...",
  "status": "Draft",
  "sequence": 4,
  "rowVersion": 4
}
```

---

# Update Draft Line

PATCH /api/v1/orders/{orderId}/lines/{lineId}

Only Draft.

```json
{
  "quantity": 3,
  "modifiers": ["..."],
  "rowVersion": 4
}
```

---

# Delete Draft Line

DELETE /api/v1/orders/{orderId}/lines/{lineId}

Only Draft. Requires `rowVersion` (`If-Match` header or body).

---

# Fire Batch

POST /api/v1/orders/{orderId}/fire

Request

```json
{
  "rowVersion": 3
}
```

Response

```json
{
  "fireBatchId": "...",
  "firedLines": 5,
  "rowVersion": 4
}
```

---

# Kitchen Queue

GET /api/v1/kds/stations/{stationId}/tickets

Returns

Lines grouped by:

Order

Station

FireBatchId

Ordered by fire time then `sequence`.

---

# Kitchen Status

POST /api/v1/kds/lines/{lineId}/start

Fired → Preparing

---

POST /api/v1/kds/lines/{lineId}/ready

Preparing → Ready

---

# Waiter Served

POST /api/v1/orders/{orderId}/lines/{lineId}/served

Ready → Served

---

# Void

POST /api/v1/orders/{orderId}/lines/{lineId}/void

Request

```json
{
  "reason": "Customer cancelled",
  "rowVersion": 6
}
```

> Role-gated per BR-M; a void while `Preparing` sets a waste flag in the `OrderEvent`
> (server-derived from the line's current state).

---

# Cancel Order

POST /api/v1/orders/{orderId}/cancel

Request

```json
{
  "reason": "...",
  "rowVersion": 4
}
```

> Permitted only when no line has reached `Served` (Order → Cancelled, State Machines §2).

---

# Order Tracking

GET /api/v1/orders/{orderId}

Returns

Order

Lines

Statuses

Totals

RowVersion

---

# Close Order

POST /api/v1/orders/{orderId}/close

Request

```json
{
  "rowVersion": 7
}
```

Response

```json
{
  "status": "Completed",
  "receiptId": "..."
}
```

> `receiptId` equals the `orderId`. There is **no separate Receipt entity** in Phase 1 — the
> receipt is the completed order's snapshotted totals (BR-B-06).

---

# Receipt

GET /api/v1/receipts/{receiptId}

Returns the printable receipt for the completed order (`receiptId` == `orderId`).

---

# SignalR Hub

Hub

/posHub

Events

OrderFired

OrderLineUpdated

OrderLineVoided

TableStatusChanged

MenuAvailabilityChanged

OrderCompleted

> KDS alerts (BR-M-04) map to hub events: **NEW** → `OrderFired`, **VOID** →
> `OrderLineVoided`, **CHANGE** → `OrderLineUpdated`. Hub authentication and group membership
> (station vs order/waiter groups) — see "Open decisions" in CLAUDE.md.

---

# Concurrency

Every write operation requires:

RowVersion

Example

```http
If-Match: W/"5"
```

or

```json
{
  "rowVersion": 5
}
```

If mismatch:

409 Conflict

ProblemDetails

```json
{
  "title": "Concurrency conflict",
  "status": 409
}
```

---

# Validation Errors

422

ProblemDetails

---

# Unauthorized

401

---

# Forbidden

403

---

# Not Found

404

---

# Error Format

RFC7807

Example

```json
{
  "type": "https://httpstatuses.com/409",
  "title": "Concurrency conflict",
  "status": 409,
  "detail": "Order has been modified.",
  "traceId": "..."
}
```

---

# Versioning

Current Version

v1

Route Prefix

/api/v1/  (applied to every route above)

Future versions:

v2

v3

---

# Future APIs (Not Phase 1)

Reserved endpoints

/api/v1/payments

/api/v1/reservations

/api/v1/inventory

/api/v1/customers

/api/v1/loyalty

/api/v1/delivery

No implementation in Phase 1.
