# Architecture Overview (Phase 1)

> This document describes the high-level architecture of the Restaurant POS platform.
> It explains **why** architectural decisions were made, not only **what** was chosen.

---

# 1. Vision

The system is a **multi-tenant Restaurant POS platform** supporting restaurants, cafés,
food courts, and similar businesses.

Phase 1 focuses on:

- Waiter Tablet
- Kitchen Display System (KDS)
- Cashier
- Basic Manager Configuration

Future phases will extend the same core with:

- Payments
- Inventory
- Delivery
- Reservations
- Loyalty
- Reporting

The architecture must allow these features to be added **without redesigning the core domain**.

---

# 2. Architectural Style

The backend is implemented as a **Modular Monolith**.

Reasons:

- Easier deployment
- Faster development
- Strong transactional consistency
- Lower operational complexity
- Clear future migration path to microservices

Every module owns its own application logic while sharing one database.

---

# 3. Technology Stack

Backend

- .NET 10
- ASP.NET Core
- EF Core
- PostgreSQL 16
- Redis 7
- SignalR

Frontend

- Flutter (Waiter Tablet)
- React (KDS)
- React (Cashier)
- React (Manager)

Infrastructure

- Docker
- Docker Compose

---

# 4. Solution Structure

```
backend/

    Pos.Api

    Pos.Application

    Pos.Domain

    Pos.Infrastructure

tests/

    Pos.Domain.Tests

    Pos.Application.Tests
```

Dependency direction:

```
Api
 ↓
Application
 ↓
Domain

Infrastructure
 ↑
Application
```

Domain never depends on Infrastructure.

---

# 5. Multi-Tenancy

Every business is a Tenant.

Every Tenant owns:

- Branches
- Menu
- Tables
- Orders
- Users

Isolation is enforced using PostgreSQL Row Level Security (RLS).

Application code must never rely only on WHERE tenant_id = ...

---

# 6. Communication

Waiter

↓

REST API

↓

Backend

↓

Database

↓

SignalR

↓

Kitchen Display

↓

SignalR

↓

Waiter

The Backend is the single source of truth.

Clients never communicate directly.

---

# 7. Kitchen Architecture

The kitchen does NOT own Orders.

It consumes **Fire Batches**.

Each fire action creates a unique:

FireBatchId (Guid)

The KDS renders one ticket per:

(Order + Station + FireBatchId)

No KitchenTicket entity exists in Phase 1.

If future requirements demand it, KitchenTicket can be introduced
without redesigning OrderLine.

---

# 8. Order Philosophy

Order

↓

OrderLine

↓

Modifier

Every OrderLine has its own lifecycle.

Order state is derived from OrderLine states.

---

# 9. Real-Time Updates

SignalR is used for:

- Fire Batch
- Status Changes
- Menu Availability
- Table Status

Redis acts as SignalR Backplane.

---

# 10. Offline Strategy

Waiter tablet supports temporary offline work.

Draft lines may be queued locally.

Fire operation requires server confirmation.

Conflict resolution uses:

Order.RowVersion

No last-write-wins behavior is allowed.

---

# 11. Audit Strategy

Every important business action creates an immutable OrderEvent.

OrderEvent is:

- Append-only
- Never updated
- Never deleted

Audit data must answer:

Who?

When?

What changed?

Why?

---

# 12. Pricing Strategy

Prices are snapshotted.

Modifier prices are snapshotted.

Tax totals are snapshotted.

Service totals are snapshotted.

Historical orders never change.

---

# 13. Security

Authentication

JWT

Authorization

Role-based

Roles

- Owner
- Manager
- Cashier
- Waiter
- Kitchen

Every API validates:

Tenant

Branch

Role

Permissions

---

# 14. Build Strategy

Development follows Vertical Slices.

Each slice contains:

UI

↓

API

↓

Application

↓

Domain

↓

Infrastructure

↓

Tests

↓

Integration

No horizontal development is allowed.

---

# 15. Non Goals (Phase 1)

Not included:

- Payments
- Inventory
- Reservations
- Loyalty
- Delivery
- KitchenTicket Entity
- Split Bill

These are future seams already supported by the domain.

---

# 16. Architecture Principles

- Domain First
- Backend is the source of truth
- UI is generated from the domain
- Events instead of hidden mutations
- Immutable audit
- Snapshot historical data
- Small vertical slices
- Additive evolution
