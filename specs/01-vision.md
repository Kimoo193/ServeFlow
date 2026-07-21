# Restaurant / Cafe POS — Vision (Phase 1)

> **Status:** Phase 1 (MVP) baseline. This document defines *why* the system exists and
> *where its boundaries are*. It is the reference the other three documents derive from.

---

## 1. The problem

Small-to-mid food & beverage businesses (restaurants, cafes, food courts, cloud kitchens)
in Egypt and the Gulf need an affordable, self-hostable POS that handles table-service
ordering and kitchen coordination in real time. Existing options are either expensive,
SaaS-locked, or over-featured for a single-branch operation that is just starting.

## 2. What we are building (one line)

A multi-tenant POS where a **waiter takes orders on a tablet**, the **kitchen sees them
live** on a display, and the **cashier closes the bill and prints a receipt** — designed
so a single codebase serves a restaurant, a cafe, or a cloud kitchen without re-architecture.

## 3. Target users (Phase 1)

- **Waiter / Cashier** — takes and manages orders on a tablet, closes bills.
- **Kitchen staff** — reads and bumps order lines on a KDS screen.
- **Manager / Owner** — configures menu, tables, and staff; reviews order history.

## 4. Design philosophy (non-negotiable)

Three layers, three depths — **do not confuse them**:

| Layer | Depth in Phase 1 | Meaning |
|---|---|---|
| Domain Model | 100% designed | The complete long-term product vision. |
| Database Model | ~30% implemented | Only the Phase 1 subset becomes real tables. |
| Features | ~10% shipped | Only the minimum that delivers real value. |

**Core rule: design for extension, do not build the extension.** We pay for the cheap
*seams* that prevent re-architecture, and defer the expensive *features* until they are
actually needed. A deferred feature that hangs off a clean seam is an *addition*, never a
*rewrite*.

## 5. Scope — Phase 1 (MVP)

**In scope**

- Multi-tenant, single operating branch (branch dimension present in schema from day one).
- Menu management: categories, items, reusable modifier groups, combos, availability states.
- Dining area: sections and tables.
- Order taking: draft → fire, sequenced lines, per-line lifecycle.
- Kitchen Display System (KDS) per station, driven by fire batches.
- Order modification with a full audit trail.
- Receipt with configurable tax and service charge (snapshotted at close).
- Roles and permissions via **KimooKit** (first real consumer of the library).
- Real-time sync via SignalR (Redis backplane).
- Offline-first waiter tablet with conflict-safe sync.
- Thermal (ESC/POS) receipt printing.

**Deferred (not in Phase 1)**

Payments, split / merge / move table, reservations, inventory, discount/promotion engine,
delivery / drive-thru execution, QR customer ordering, loyalty, analytics.

## 6. Roadmap

- **Phase 1 (MVP)** — core ordering + KDS + receipt. *(this document set)*
- **Phase 2** — payments, split bill, move/merge table, reservations, customer accounts, discounts.
- **Phase 3** — inventory, purchasing, loyalty, delivery integrations, QR ordering, analytics.

## 7. Non-functional requirements (verifiable)

| ID | Requirement |
|---|---|
| NFR-1 | A fired order line **shall** appear on the correct KDS station in ≤ 2 s (p95) under normal single-branch load. |
| NFR-2 | The waiter tablet **shall** accept new orders with no network and sync within 10 s of reconnection, with **no lost or duplicated lines**. |
| NFR-3 | No tenant **shall** ever read or write another tenant's data; isolation is enforced at the database (RLS), not by application code alone. |
| NFR-4 | Every create / modify / void / refire on an order **shall** be fully reconstructable from `OrderEvent` (who, what, when, why). |
| NFR-5 | Two staff editing the same order concurrently **shall never** silently lose a change (optimistic concurrency). |
| NFR-6 | The system **should** run on a single modest VPS (2 vCPU / 8 GB) for one branch. |

## 8. Explicit non-goals (Phase 1)

This is **not** a payment processor, **not** an accounting system, and **not** a delivery
dispatch platform. Those integrate in later phases; treating them as MVP scope would sink
the MVP.
