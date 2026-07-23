# ServeFlow — Multi-Tenant Restaurant / Cafe POS (Phase 1)

ServeFlow is a multi-tenant POS platform for restaurants, cafes, and cloud kitchens. In
Phase 1 a **waiter** takes orders on a tablet (Flutter — out of scope here), the **kitchen**
sees them live on a KDS display, the **cashier** closes the bill and prints a receipt, and a
**manager** configures the venue and reviews history. One .NET backend serves all surfaces,
with tenant isolation enforced at the database level by PostgreSQL Row-Level Security (FORCE
ROW LEVEL SECURITY).

## Feature summary

| Surface | What it does |
|---|---|
| **Backend** | Multi-tenant API (RLS), JWT auth, order lifecycle (Draft→Fired→Preparing→Ready→Served/Voided), fire batches, SignalR live KDS push, close + receipt/bill-preview, menu/modifier/station CRUD, floor/section/table management, staff & role-permissions matrix, dashboard, order-history, ESC/POS print |
| **KDS** (Angular) | Live station board by `(order, fire_batch)`, Start / Ready bumps, NEW/VOID/CHANGE alerts via SignalR, multi-terminal station sync |
| **Cashier** (Angular) | Open orders list, bill preview with tax/service, close (snapshot totals), receipt / print |
| **Manager** (Angular) | Dashboard (today's orders), Menu config (categories/items/modifiers/groups/combos/stations), Floor (sections/tables), Staff & roles/permissions, Order history |

## Architecture

Clean Architecture split across four .NET projects:

```
backend/
├── Pos.sln
├── src/
│   ├── Pos.Domain/          # entities, value objects, enums, invariants, domain events
│   ├── Pos.Application/     # use-cases, interfaces (ITenantContext, IClock…)
│   ├── Pos.Infrastructure/  # EF DbContext, migrations, RLS, Redis, SignalR impl
│   └── Pos.Api/             # controllers, hubs, DI composition, middleware
└── tests/
    ├── Pos.Domain.Tests/
    └── Pos.Application.Tests/
```

Three Angular apps (standalone + signals) share a `@serveflow/shared` library:

```
frontend/
├── angular.json
├── projects/
│   ├── kds/          # Kitchen Display System
│   ├── cashier/      # Cashier close/receipt
│   ├── manager/      # Manager configuration
│   └── shared/       # @serveflow/shared library
```

### Tech stack

| Layer | Technology |
|---|---|
| Backend | .NET 10, ASP.NET Core, EF Core, PostgreSQL 16, Redis 7, SignalR, xUnit |
| Auth | JWT (dev token endpoint); KimooKit replaces in later slice |
| Frontend | Angular 22 (standalone/signals), SignalR via `@microsoft/signalr` |
| Infra | Docker Compose (Postgres + Redis), Coolify (deploy target) |

## Prerequisites

- .NET 10 SDK (`dotnet --version` → `10.x`)
- Node.js + npm (LTS)
- Docker + Docker Compose
- `dotnet-ef` tool: `dotnet tool install --global dotnet-ef`

## Run it locally

### 1. Start infrastructure

```bash
docker compose up -d
```

Postgres starts on `:5442` (database `pos`, owner role `pos_owner`), Redis on `:6389`.

### 2. Apply the schema

Run the EF migration with the **owner** connection (RLS policy DDL needs table-owner
privileges — the app's `pos_app` role cannot create tables):

```bash
cd backend
dotnet ef database update --project src/Pos.Infrastructure --startup-project src/Pos.Api \
  --connection "Host=localhost;Port=5442;Database=pos;Username=pos_owner;Password=pos_owner_dev_password"
```

### 3. Start the API

```bash
cd backend
dotnet run --project src/Pos.Api --urls http://localhost:5299
```

Development mode auto-runs `DevSeeder` (seeds tenant + branch + settings + per-role users —
see *Test accounts* below) and exposes `POST /dev/token` for minting JWTs. The API listens
on port `5299` matching the frontend environment configs.

### 4. Start a frontend app

```bash
cd frontend
npm install
```

Then serve each app on its own port:

```bash
npx ng serve manager --port 4200   # Manager
npx ng serve cashier --port 4201   # Cashier
npx ng serve kds     --port 4202   # KDS
```

Each app opens a **connect** screen. Paste the tenant / branch / user GUIDs from the table
below; the app exchanges them for a JWT via `/dev/token` and navigates to its main screen.
The role is hard-coded per app (Manager app → `Manager`, Cashier app → `Cashier`, KDS →
`Manager`).

## How to test

```bash
cd backend && dotnet test
```

The integration tests spin up their own hermetic Postgres 16 containers via
Testcontainers — the docker-compose services do **not** need to be running for tests.
Currently **80 tests** across domain rules, RLS isolation, permission enforcement, order
lifecycle, fire batches, close/receipt, and offline idempotency.

Frontend build check (no Jest/Karma tests in Phase 1):

```bash
cd frontend && npx ng build manager && npx ng build cashier && npx ng build kds
```

## Test accounts & connect flow

All users belong to the seed **Dev Tenant** (`00000000-0000-0000-0000-000000000001`) and
**Main Branch** (`00000000-0000-0000-0000-000000000002`).

### Per-role users

| Name | Role | User ID (GUID) |
|---|---|---|
| Dev Owner | Owner | `00000000-0000-0000-0000-000000000003` |
| Dev Manager | Manager | `00000000-0000-0000-0000-000000000004` |
| Dev Cashier | Cashier | `00000000-0000-0000-0000-000000000005` |
| Dev Waiter | Waiter | `00000000-0000-0000-0000-000000000006` |
| Dev Kitchen | Kitchen | `00000000-0000-0000-0000-000000000007` |

### Connect through the web apps

Open one of the Angular apps, fill in:
- **Tenant ID:** `00000000-0000-0000-0000-000000000001`
- **Branch ID:** `00000000-0000-0000-0000-000000000002`
- **User ID:** pick a GUID from the table above

Each app sends its own **fixed role** to `/dev/token`:
- **Cashier** app → `Cashier`
- **Manager** app → `Manager`
- **KDS** app → `Manager` (the KDS is a Manager-scoped surface)

> **Tip:** Use the `Dev Manager` user (`…0004`) for the Manager and KDS apps, and `Dev
> Cashier` (`…0005`) for the Cashier app. The `Dev Waiter` / `Dev Kitchen` users are
> intended for the (separate) Flutter waiter app and future KimooKit login — they have no
> granted permissions in the seed.

### Permission model

- **Owner** bypasses all permission checks (`PermissionAuthorization.cs:12`).
- **Manager** is granted **all** six permissions by the seed (`RolePermissions`).
- **Cashier** is granted `CloseOrder` + `ViewHistory` only.
- **Waiter** and **Kitchen** have **no** granted permissions — calling any
  `RequirePermission` endpoint returns 403.

Try it with curl:

```bash
# Get an Owner token (can do everything)
curl -s -X POST http://localhost:5299/dev/token \
  -H 'Content-Type: application/json' \
  -d '{"tenantId":"00000000-0000-0000-0000-000000000001","branchId":"00000000-0000-0000-0000-000000000002","userId":"00000000-0000-0000-0000-000000000003","role":"Owner"}'
```

```bash
# Get a Waiter token, then try a Manager-only endpoint → 403
TOKEN=$(curl -s -X POST http://localhost:5299/dev/token \
  -H 'Content-Type: application/json' \
  -d '{"tenantId":"00000000-0000-0000-0000-000000000001","branchId":"00000000-0000-0000-0000-000000000002","userId":"00000000-0000-0000-0000-000000000006","role":"Waiter"}' | python3 -c 'import sys,json;print(json.load(sys.stdin)["accessToken"])')

curl -s -X POST http://localhost:5299/users \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"displayName":"Intruder","role":"Manager"}'
# → 403 {"title":"Insufficient permissions","detail":"Requires the ManageStaff permission."}
```

### Permissions reference

| Permission | Endpoints gated | Seed grants |
|---|---|---|
| `VoidLine` | Voiding fired+ lines, quantity-increase | Owner, Manager |
| `CloseOrder` | `POST …/close` | Owner, Manager, Cashier |
| `EditMenu` | CRUD categories/items/modifiers/combos | Owner, Manager |
| `ManageFloor` | CRUD sections/tables/stations | Owner, Manager |
| `ManageStaff` | CRUD users, role-permissions | Owner, Manager |
| `ViewHistory` | `GET /dashboard/summary`, `GET /orders/history` | Owner, Manager, Cashier |
