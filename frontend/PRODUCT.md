# PRODUCT.md — ServeFlow web apps

## Register
**Product.** These are working tools used under time pressure, not marketing surfaces. Design serves the task; the interface should disappear into the work. Familiarity and consistency beat novelty.

## Product
A multi-tenant restaurant/cafe POS. Three web surfaces share one backend, one domain, and one design system ("Service Spruce"):
- **KDS** (kitchen display) — a dark, glanceable wall board. Tickets in, bump out. Colour and status over text.
- **Cashier** — review-and-close: open orders → bill → print → close. Light theme.
- **Manager** — minimal config surface: Dashboard, Menu, Floor (sections/tables/stations), Order history, Staff & roles + permissions. Light theme. (A Flutter waiter tablet is out of scope for these docs.)

## Target users & context
- **Kitchen** reads the KDS from across a hot, bright kitchen — legibility at distance is non-negotiable.
- **Cashiers** work a till quickly, often one-handed on a tablet.
- **Managers** configure the menu, floor, and staff on a desktop between services.
- Bilingual AR/EN (Egypt/Gulf) is a hard requirement: full RTL via logical properties; self-hosted Rubik (AR+Latin) + IBM Plex Mono.

## The job on each screen
The domain generates the UI. Every screen is a projection of domain state (the `OrderLine` state machine, availability, fire batches, role permissions). The primary task is always one thing: bump a ticket, close a bill, edit an item, grant a permission.

## Brand personality (3 words)
**Disciplined. Legible. Operational.** Quiet and unfussy, with exactly one bold thing per surface (the KDS status spine + aging timer). Confidence through restraint, not decoration.

## Anti-references
- Not a consumer food-delivery app (no playful gradients, no marketing hero energy).
- Not a generic SaaS dashboard cliché (no gradient-accented hero-metric template, no identical icon-card grids, no all-caps tracked eyebrow on every section).
- Not "dark because tools look cool dark" — KDS is dark because it's a wall screen in a bright kitchen; Cashier/Manager are light because they're read up close under normal light.

## Accessibility
WCAG AA contrast (verify amber/green on the dark KDS bg), always-visible keyboard focus, full `prefers-reduced-motion` support (pulses become instant), full RTL. Status is never conveyed by colour alone — pair with label/shape.

## Strategic design principles
1. One colour means one thing everywhere — the palette IS the OrderLine state machine; status hues are never reused for chrome.
2. Snapshots, not decoration: numbers (money, counts, timers) use tabular mono and are treated as data.
3. Consistency over surprise — one component vocabulary across all three apps; delight is reserved for moments (a new-ticket pulse), never pages.
