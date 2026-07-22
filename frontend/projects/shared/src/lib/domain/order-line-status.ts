/**
 * The OrderLine lifecycle — the heart of the domain (specs/03-state-machines.md).
 * Draft → Fired → Preparing → Ready → Served, plus terminal Voided.
 * The string values match the backend enum names exactly (e.g. "Preparing").
 */
export type OrderLineStatus =
  | 'Draft'
  | 'Fired'
  | 'Preparing'
  | 'Ready'
  | 'Served'
  | 'Voided';

/** CSS custom-property name carrying each status colour (see service-spruce.css). */
export const STATUS_COLOR_VAR: Record<OrderLineStatus, string> = {
  Draft: 'var(--sf-draft)',
  Fired: 'var(--sf-fired)',
  Preparing: 'var(--sf-preparing)',
  Ready: 'var(--sf-ready)',
  Served: 'var(--sf-served)',
  Voided: 'var(--sf-voided)',
};

/** Short, glanceable label for the KDS. Kept terse on purpose. */
export const STATUS_LABEL: Record<OrderLineStatus, string> = {
  Draft: 'Draft',
  Fired: 'New',
  Preparing: 'Cooking',
  Ready: 'Ready',
  Served: 'Served',
  Voided: 'Void',
};
