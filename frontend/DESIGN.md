# DESIGN.md — Service Spruce

Visual system shared by KDS, Cashier, and Manager. Tokens live in `projects/shared/src/design/service-spruce.css`; the shared component vocabulary lives in `projects/shared/src/design/ui.css`.

## Theme
Two surfaces, one language. **Light** (Cashier/Manager): porcelain background, ink text. **Dark** (KDS): blue-ink wall screen. Chosen for the physical scene, not fashion — KDS is read across a bright kitchen; Cashier/Manager up close.

## Color
- **Brand**: spruce `#0F5F52`, spruce-deep `#0A463C`, mint `#34D8B4` (the "flow" pulse, used sparingly). Brand sits deliberately outside the status hues so it never competes with a state colour.
- **Light surfaces**: porcelain `#F5F3EF` (bg), ink `#1A2420` (text), white panels.
- **Status (the OrderLine state machine — fixed, never reused for chrome)**: Draft `#8A97A0` · Fired `#5B8DEF` · Preparing `#F5A524` · Ready `#2FBF71` · Served `#5A6B66` · Voided `#EF4444`.
- Strategy: **Restrained** — tinted neutrals + spruce for primary actions/selection; status hues only for status.

## Typography
- **Rubik** (self-hosted) — all UI, AR+Latin, weights 300–900, tabular figures.
- **IBM Plex Mono** — numbers that are data: money, counts, timers (`.sf-mono`, `font-variant-numeric: tabular-nums`).
- Fixed rem scale (product register), ratio ~1.2. Steps in `ui.css` (`--fs-xs`…`--fs-2xl`).
- Never a display font in a UI label; one family (Rubik) carries headings→labels→data.

## Components (ui.css — one vocabulary, global, not per-component)
`.btn` (+ `.btn-primary` spruce, `.btn-ghost`, `.btn-danger`, `.btn-sm`) with full default/hover/focus/active/disabled states · `.field` label+control · `.input`/`.select` · `.card` (single-level, never nested) · `.badge` + `.badge--<status>` mapped to the state tokens · `.data-table` · `.banner` (`.banner--error`, `.banner--info`) · `.skeleton` loaders · `.empty` state that teaches · `.section-head`. Same button, same field, same table on every surface.

## Layout & motion
- App shell: fixed side nav + scrolling content. Responsive is structural (nav collapses), never fluid type.
- Radii: 12px (`--sf-radius`), 8px (`--sf-radius-sm`). Rhythm base 16px (`--sf-gap`).
- Motion: 150–250ms, ease-out; conveys state only (hover, selection, the one KDS new-ticket mint pulse / void flash). Full `prefers-reduced-motion` fallback. No page-load choreography.
- Quality floor: always-visible `:focus-visible` (mint outline), WCAG AA, status never colour-alone.
