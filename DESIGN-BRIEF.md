# ServeFlow — UI/UX Redesign Brief

> **For the designer:** This document describes the *system, the users, and every existing
> screen* so you can design a fresh UI/UX. It intentionally does **not** specify any colors,
> hex values, or a palette. **Choosing the color language is your job** — the type scale,
> spacing, layout, motion, and the *meaning* of each state are described here; the hues are
> yours to select. Where a color currently carries meaning (e.g. an order state), the brief
> names the *meaning*, not the color, so you can map your own palette onto it.

---

## 1. What ServeFlow is

A **multi-tenant restaurant / cafe Point-of-Sale (POS)**. One backend serves many venues
(restaurants, cafes, cloud kitchens). Staff work across four surfaces on one shared system:

| Surface | Device | Who uses it | What it does |
|---|---|---|---|
| **Waiter** | Tablet (Flutter — *not* in this brief) | Waiters | Take orders at the table, fire to kitchen |
| **KDS** (Kitchen Display) | Wall-mounted screen | Kitchen line cooks | See fired tickets live, bump them cooking → ready |
| **Cashier** | Tablet / desktop | Cashiers | Review the bill, close it, print the receipt |
| **Manager** | Desktop | Owners / Managers | Configure menu, floor plan, staff, and view history |

**This brief covers the three web surfaces: KDS, Cashier, and Manager.** They are three separate
apps that share one design system and one component kit. A redesign should keep them feeling like
**one product family** while respecting that each has a very different context of use (see §3).

### The operational reality that should shape the design
- **KDS is a glanceable wall screen.** Cooks read it from a distance, hands busy, under time
  pressure. Big type, high contrast, minimal chrome, motion that signals *change* (a new ticket
  arriving, an item being voided) rather than decoration.
- **Cashier is transactional and fast.** One job at a time: pick an order, read the bill, close it.
  Large touch targets, unambiguous money, a clear "can I close this yet?" signal.
- **Manager is a configuration surface.** Forms, tables, CRUD. Density and clarity matter more
  than flourish; it is used sitting down, deliberately.

---

## 2. Core domain concepts (so screen states make sense)

- **Order** — a bill in progress at a table or for takeaway. Has a status derived partly from its lines.
- **OrderLine** — one item on an order. Moves through a **lifecycle** that the whole system color-codes:

  **Draft → Fired → Preparing → Ready → Served** (plus **Voided**).

  These six states are the backbone of the visual language. **Each state must be instantly
  distinguishable at a glance.** Today they map to six distinct colors; you should design six
  distinct, accessible signals (color + optionally shape/label) in your own palette. Their
  *meaning*:
  - **Draft** — added, not yet sent to the kitchen (quiet, inactive).
  - **Fired** — just sent to the kitchen; the kitchen has *not* started it (needs attention / "new").
  - **Preparing** — the kitchen is actively cooking it (in-progress / warm).
  - **Ready** — cooked, waiting to be served (positive / go).
  - **Served** — delivered to the table (done / neutral, receded).
  - **Voided** — cancelled (destructive / alert; never silently disappears — it flashes then collapses).
- **Fire batch** — a group of lines sent to the kitchen together. A **KDS ticket** = the lines of
  one order, at one station, from one fire batch.
- **Station** — a kitchen area (e.g. Grill, Bar). Fired items route to the station's board.
- **Table** — belongs to a floor **Section**. A table has at most one open order at a time.
- **Roles** — Owner, Manager, Cashier, Waiter, Kitchen. Permissions are configurable per role.

---

## 3. Design system today ("Service Spruce") — what to keep vs. re-decide

The current system is called **Service Spruce**. Treat the following as **structure to preserve**
and **palette to replace**:

**Keep the ideas (structure):**
- **Bilingual by requirement.** Must support **Arabic + English**, including full **RTL**. Use
  logical CSS properties (inline/block), and pick type that covers both scripts well. Current
  choice: **Rubik** (UI/display) + a **monospaced** face for numbers/money/timers/IDs — money,
  counts, and timers are always set in mono so "data" is unmistakable. You may re-choose the
  typefaces, but keep the *"numbers look like data"* principle.
- **A shared component kit** across all three apps: buttons (primary / ghost / danger, plus a
  small size), form fields (label + input + select), cards, badges, data tables, banners
  (error / warn / info), skeleton loaders, and a standard **empty state** (title + hint).
- **State-driven color.** A color means the same thing in every app (see the six line states above).
- **Two contexts, one family.** Manager & Cashier are **light** surfaces; KDS is a **dark wall
  screen**. The kit renders on both by remapping neutral surface variables. Keep this: your palette
  needs a light theme (Manager/Cashier) and a dark theme (KDS) that clearly belong together.

**Re-decide (yours to choose):**
- The entire **color palette** — brand hue(s), neutrals, and the six state colors. No constraints
  from this brief beyond: (a) the six line states must be mutually distinguishable and
  **WCAG AA** legible on their backgrounds, including on the dark KDS surface; (b) brand color
  should not be confusable with any state color.
- Visual tone, density, radii, shadows, motion style, iconography, and the "signature" moments.

**Accessibility floor (non-negotiable):** visible keyboard focus, WCAG AA contrast, honor
`prefers-reduced-motion` (pulses/flashes become instant), responsive down to tablet/mobile, and
horizontal-scroll-safe wide content (tables, boards).

---

## 4. The screens — every existing page

Each app is a standalone Angular app. Routes and states below are exhaustive for the current build.
Screens marked **"connect"** are dev/entry gates (a venue would replace these with real login);
still design them, but they are low-traffic.

### 4.1 Manager app (light, desktop) — persistent left sidebar

**Shell / navigation (present on every Manager page except Connect):**
- Left **sidebar**: brand mark + "ServeFlow" + the signed-in **role** chip; an eyebrow label
  "Manage"; nav links **Dashboard · Menu · Floor · History · Staff** (active state highlighted);
  a **Disconnect** button at the bottom.
- Main content area with a page header (title, optional subtitle) per page.

**M1 — Manager Connect** (`/`)
- Centered card. Brand mark + "ServeFlow" + "Manager" badge. Lead text
  "Connect to configure the menu, floor, and staff." Three mono inputs (Tenant ID, Branch ID,
  User ID), an inline error line, and a primary button **"Open manager desk"** (shows
  "Connecting…" while busy). *This is the entry gate.*

**M2 — Dashboard** (`/dashboard`)
- Page header "Dashboard" + subtitle "Today · \<date\>".
- A row of **stat cards**: **Orders today** and **Open now**, each a big mono number + label +
  a small note ("Placed since midnight (UTC)", "Awaiting service or payment").
- States: **loading** (skeleton stat cards), **loaded**, **error banner** (dismissible).
- *This is a summary landing page; it is currently sparse — a redesign could make it the at-a-glance
  pulse of the venue, but do not invent metrics the backend doesn't provide (only these two exist today).*

**M3 — Menu** (`/menu`)
- Page header "Menu" + a dismissible error banner slot.
- **Three tabs**: **Categories & Items**, **Modifier Groups**, **Combos**.
  - **Categories & Items panel** — the core menu CRUD: list of categories, each expandable to its
    items; create/edit/delete categories; add/edit items; set item **availability**
    (Available / Out of stock / Hidden); assign an item's **station**. Many inline forms and
    per-row action buttons (Edit / Delete with a **confirm** two-step), an empty state.
  - **Modifier Groups panel** — a library of modifier groups (e.g. "Milk choice") and their options;
    full CRUD with inline forms, confirm-to-delete, empty state.
  - **Combos panel** — bundle products made of component items; CRUD with inline forms,
    confirm-to-delete, empty state.
- Interaction pattern throughout: **inline create/edit forms** (not modals), and **two-step
  destructive confirm** (Delete → Confirm / Cancel).

**M4 — Floor** (`/floor`)
- Page header "Floor". **Two panels side by side:**
  - **Sections & Tables** — create sections; create tables inside a section (Section select + Label);
    each table row shows its **label** and an **occupancy badge** (Occupied vs. free); Edit + confirm-delete.
    Empty state "No sections yet".
  - **Stations** — create/edit/delete kitchen stations; each is a simple named row with actions.
    Empty state explains stations route fired items to the right board.

**M5 — History** (`/history`)
- Page header "Order history" + "\<n\> most recent" subtitle.
- A **data table**: columns **Created** (mono time), **#** (daily number), **Table / Customer**,
  **Type**, **Status** (state badge), **Items** (count), **Total** (money). Right-aligned numeric cols.
- States: **loading** (skeleton rows), **empty** ("No orders yet"), **loaded**, **error banner**.

**M6 — Staff & roles** (`/staff`)
- Page header "Staff & roles". **Two tabs:**
  - **Staff directory** — list of staff **user cards**: name, role, and an **Active/Inactive** badge.
    "Add staff" opens an inline form (Display name + Role select). Edit inline (name, role, Active
    checkbox). **Deactivate** is a two-step confirm. Empty state "No staff yet".
  - **Permissions** — a **matrix table**: rows = roles, columns = permissions, cells = checkboxes;
    a per-row **Save** button. Helper note explains "Owner always has every permission; other roles
    get what you grant here." Horizontally scrollable (many permission columns). Loading state.

### 4.2 Cashier app (light, tablet/desktop)

**C1 — Cashier Connect** (`/`)
- Same connect pattern as Manager: centered card, brand + "Cashier" badge, lead
  "Connect to close bills and print receipts" (equivalent), three mono ID inputs, primary
  **"Open cashier"** button, inline error.

**C2 — Open orders** (`/orders`)
- Topbar: title "Open orders" + a small **Refresh** button (shows "Refreshing…").
- A **grid of order cards**. Each card shows: **Table \<label\>** *or* **Takeaway + #dailyNo**;
  the order **type**; optional **customer name**; an item **count**; and a **status badge** that is
  the cashier's key signal:
  - **"Ready to close"** when nothing is still cooking (positive / go), **vs.**
  - **"In kitchen · N cooking"** when items remain (waiting / caution).
- Whole card is a link into the bill. States: **loading** (skeleton cards), **empty**
  ("No open orders"), **loaded**, **error banner**.

**C3 — Order review / bill** (`/orders/:id`)
- Topbar: **back** chevron, title (**"Bill review"** or **"Bill · closed"**), a short mono **order id**.
- Two banner slots: error, and a **warn** banner for close failures.
- **Preview state** (bill not yet closed):
  - A **bill table**: Qty (mono), Item, Price (mono), Total (mono); modifier lines shown indented
    under their item ("+ Extra shot").
  - A **totals block**: Subtotal, Service, Tax, optional Discount, and a bold **grand Total** — all mono.
  - **Actions**: if the kitchen isn't done, a hint "Waiting on the kitchen — N items still cooking"
    and the **Close bill** button is **disabled**; once ready, Close is enabled (shows "Closing…").
- **Closed state** (after closing): a "Closed \<timestamp\>" line, the same bill + totals (now a
  receipt), a **Print receipt** button (shows "Printing…", then a result message), and a
  **"Back to open orders"** link.
- **Loading** state while fetching.
- *The single most important cashier signal is "can I close this bill yet?" — design that
  affordance (the badge on C2, the disabled/enabled Close button + hint on C3) to be unmistakable.*

### 4.3 KDS app (dark wall screen)

**K1 — Station Connect** (`/`)
- Dark centered card, brand + **"Kitchen Display"** badge, lead "Connect this screen to a station."
- Four mono inputs: Tenant ID, Branch ID, **Station ID** (the station this screen shows), User ID.
- A mint/accent primary button **"Open station board"** (shows "Connecting…"), inline error.

**K2 — Station Board** (`/board/:stationId`) — *the signature screen*
- Topbar: **back** chevron; the **station name** (large, heavy); a **live connection indicator**
  (a dot + status text: connecting / connected / reconnecting / disconnected — this must be honest
  and visible, cooks need to trust the board is live); an **"N active"** ticket count (mono).
- A **board = grid of ticket cards** (see K3). States: **loading** ("Loading the board…"),
  **empty** ("All clear. New tickets appear here the moment they're fired."), **live**,
  and a **banner** if realtime updates are unavailable.
- Realtime: a **new ticket** arrives and animates in (a single pulse, not a loop); a **voided**
  line flashes then collapses — **never a silent disappearance** (a cook must notice cancellations).

**K3 — Ticket card** (component on the board) — *carries the brand voice*
- A card per (order + fire batch). Signature element: a **thick colored left "spine"** whose color
  = the ticket's line **state** (Fired / Preparing / etc.).
- **Header**: short order # · "batch \<id\>" · a **live aging timer** (mono) that **heats up as the
  ticket ages** (calm → warm → hot) — a real operational signal for "this has been waiting too long."
- **Lines**: each line = quantity (mono) + item name + an **action button** driven by state:
  - **Fired** line → a **"Start"** button (kitchen begins it → Preparing).
  - **Preparing** line → a **"Ready"** button (marks it cooked → leaves the active board).
  - **Voided** line → a **"VOID"** tag, flashing then collapsing.
  - Modifiers listed under the line ("+ No onions").
- Buttons are **big touch targets** (cooks tap fast, sometimes with gloves).

---

## 5. Cross-cutting components to (re)design as one kit

These are shared primitives; redesign them once and all three apps inherit them:

1. **Buttons** — primary, ghost/secondary, danger; plus a small variant. Clear hover/focus/disabled/busy states (busy shows "…ing" label).
2. **Form field** — label + input; also a select variant; mono variant for ID/number inputs. Inline (non-modal) create/edit forms are the house style.
3. **Two-step destructive confirm** — Delete → (Confirm / Cancel) inline. Used across Menu, Floor, Staff.
4. **Card** — used for stat cards, order cards, user cards, ticket cards, connect cards.
5. **Badge** — neutral/muted, active/inactive, and the **six order-line states**; plus semantic ok/warn/danger. This is where your state palette lives.
6. **Data table** — History, and the Permissions matrix (wide → horizontal scroll).
7. **Banner** — error / warn / info; dismissible variant; an "edge" full-width variant (Cashier).
8. **Skeleton loader** — line skeletons for stat cards, tables, order cards.
9. **Empty state** — title + hint; every list/table has one. These should be reassuring and useful, not dead ends.
10. **Connection indicator** (KDS) — honest live-status dot + label.
11. **Sidebar nav** (Manager) — brand, role chip, links with active state, disconnect.

---

## 6. What the redesign should optimize for

- **One coherent family, three contexts.** Light Manager/Cashier + dark KDS that read as siblings.
- **Glanceability where it counts** (KDS board, Cashier "ready to close" signal).
- **Calm density for configuration** (Manager forms/tables) — legible, forgiving, not noisy.
- **State legibility** — the six line states are the soul of the product; make them sing in your palette.
- **Bilingual + RTL** from the start, with data set in mono.
- **Accessible by default** — AA contrast, visible focus, reduced-motion, responsive.
- **Motion with meaning** — the KDS new-ticket pulse, void flash-and-collapse, and the aging-timer heat ramp are functional signals, not decoration. Everything else stays quiet.

## 7. Explicitly out of scope
- The Flutter **Waiter** tablet app (native, separate).
- Any **payment capture** flow — "close" produces a completed order + receipt, not a payment.
- Phase 2/3 features (split/merge tables, reservations, inventory, loyalty, discounts engine).
  Do **not** design screens for features that don't exist yet.

---

### A note on color, restated
Every place this brief mentions a color (spines, badges, "ready to close," heat ramp, active/inactive),
it is describing a **meaning slot**. Fill those slots with your own palette. There is no required brand
color, no required neutrals, and no required state hues — only the requirement that the six order-line
states stay mutually distinct and accessible in both the light and the dark theme. **The palette is yours.**
