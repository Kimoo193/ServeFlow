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
| Auth | JWT via `POST /auth/login` (PBKDF2 password hashing); an OIDC provider can replace it behind the same contract |
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

Each app opens a **connect** screen. Enter the tenant / branch GUIDs plus a username and
password from the table below; the app exchanges them for a JWT via `POST /auth/login` and
navigates to its main screen. The role comes from the signed-in user — no app asserts a role
for itself.

## How to test

```bash
docker compose up -d redis     # required — see below
cd backend && dotnet test
```

The integration tests spin up their own hermetic **Postgres** 16 containers via
Testcontainers, so you do not need the compose Postgres. **Redis is different: the tests
point the SignalR backplane at `localhost:6389`, so `pos-redis` must be running.** Without
it, every endpoint that broadcasts (fire, KDS bump, availability, close) returns 500 and
roughly a third of the suite fails with errors that look nothing like a Redis problem.

Currently **105 tests** across RLS isolation, permission enforcement, password login, dev
seeding, order lifecycle, fire batches, close/receipt, and offline idempotency.

Frontend build check (no Jest/Karma tests in Phase 1):

```bash
cd frontend && npx ng build manager && npx ng build cashier && npx ng build kds
```

## Test accounts & connect flow

All users belong to the seed **Dev Tenant** (`00000000-0000-0000-0000-000000000001`) and
**Main Branch** (`00000000-0000-0000-0000-000000000002`).

### Per-role users

Every account uses the password `devpass123`.

| Name | Role | Username | User ID (GUID) |
|---|---|---|---|
| Dev Owner | Owner | `owner` | `00000000-0000-0000-0000-000000000003` |
| Dev Manager | Manager | `manager` | `00000000-0000-0000-0000-000000000004` |
| Dev Cashier | Cashier | `cashier` | `00000000-0000-0000-0000-000000000005` |
| Dev Waiter | Waiter | `waiter` | `00000000-0000-0000-0000-000000000006` |
| Dev Kitchen | Kitchen | `kitchen` | `00000000-0000-0000-0000-000000000007` |

### Connect through the web apps

Open one of the Angular apps and fill in:
- **Tenant ID:** `00000000-0000-0000-0000-000000000001`
- **Branch ID:** `00000000-0000-0000-0000-000000000002`
- **Username / Password:** an account from the table above

Sign in with the account that matches the surface — `manager` for the Manager app, `cashier`
for the Cashier app, and `kitchen` for the KDS. The KDS deliberately runs as a `Kitchen`
account: a wall screen is physically accessible, and `Kitchen` carries only
`BumpKitchenLine`, so it can work the board but cannot edit the menu or manage staff.

Each app stores its session under its own key (`sf.manager.session`, `sf.cashier.session`,
`sf.kds.session`), so two apps served from one host no longer overwrite each other.

### Permission model

- **Owner** bypasses all permission checks (`PermissionAuthorization.cs:12`).
- **Manager** is granted **every** permission by the seed (`RolePermissions`).
- **Cashier** is granted `CloseOrder`, `ViewHistory`, and `ReprintReceipt`.
- **Waiter** is granted `TakeOrder`, `FireOrder`, and `ServeLine`.
- **Kitchen** is granted `BumpKitchenLine` only.

The seed grants each missing `(role, permission)` pair on every start, so a database created
before a permission was added picks it up on the next run.

Try it with curl:

Try it with curl:

```bash
# Log in as the Owner (can do everything)
curl -s -X POST http://localhost:5299/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"tenantId":"00000000-0000-0000-0000-000000000001","branchId":"00000000-0000-0000-0000-000000000002","username":"owner","password":"devpass123"}'
```

```bash
# Log in as the Kitchen account, then try a Manager-only endpoint → 403
TOKEN=$(curl -s -X POST http://localhost:5299/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"tenantId":"00000000-0000-0000-0000-000000000001","branchId":"00000000-0000-0000-0000-000000000002","username":"kitchen","password":"devpass123"}' | python3 -c 'import sys,json;print(json.load(sys.stdin)["accessToken"])')

curl -s -X POST http://localhost:5299/users \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"displayName":"Intruder","role":"Manager"}'
# → 403 {"title":"Insufficient permissions","detail":"Requires the ManageStaff permission."}
```

`POST /dev/token` still exists in Development for scripting against an arbitrary role, but no
front-end uses it and it is absent outside Development.

### Permissions reference

| Permission | Endpoints gated | Seed grants |
|---|---|---|
| `VoidLine` | Voiding fired+ lines, quantity-increase | Owner, Manager |
| `CloseOrder` | `POST …/close` | Owner, Manager, Cashier |
| `EditMenu` | CRUD categories/items/modifiers/combos | Owner, Manager |
| `ManageFloor` | CRUD sections/tables/stations | Owner, Manager |
| `ManageStaff` | CRUD users, role-permissions | Owner, Manager |
| `ViewHistory` | `GET /dashboard/summary`, `GET /orders/history` | Owner, Manager, Cashier |
| `TakeOrder` | Open an order on a table, `POST /orders`, add/edit/remove lines | Owner, Manager, Waiter |
| `FireOrder` | `POST /orders/{id}/fire` | Owner, Manager, Waiter |
| `BumpKitchenLine` | `POST /kds/lines/{id}/start-preparing` and `/ready` | Owner, Manager, Kitchen |
| `ServeLine` | `POST /orders/{id}/lines/{lineId}/serve` | Owner, Manager, Waiter |
| `ReprintReceipt` | `POST /orders/{id}/receipt/print` | Owner, Manager, Cashier |
