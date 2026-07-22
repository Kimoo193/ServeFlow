import { OrderLineStatus } from './order-line-status';

/** A menu item as returned by GET /menu-items. */
export interface MenuItem {
  id: string;
  categoryId: string;
  stationId: string;
  name: string;
  price: number;
  availability: 'Available' | 'OutOfStock' | 'Hidden';
}

/** A modifier snapshot on a fired line (name + price delta captured at order time). */
export interface TicketLineModifier {
  name: string;
  priceDelta: number;
}

/** One line on a KDS ticket. Matches GET /kds/stations/{id}/tickets line shape. */
export interface TicketLine {
  id: string;
  itemId: string;
  sequence: number;
  quantity: number;
  unitPrice: number;
  status: OrderLineStatus;
  modifiers: TicketLineModifier[];
  /** transient UI flag: the line is flashing a void before it leaves the board */
  voiding?: boolean;
}

/**
 * A KDS ticket = one (order, station, fire_batch) group (spec §4).
 * `firstSeenAt` is client-side: the backend ticket payload carries no fire
 * timestamp, so the aging timer counts from when this board first saw the
 * ticket. For an always-on station screen that is the moment it was fired.
 */
export interface Ticket {
  orderId: string;
  fireBatchId: string;
  lines: TicketLine[];
  firstSeenAt: number; // epoch ms
  /** transient UI flags for the signature motion */
  isNew?: boolean;
  isVoiding?: boolean;
}

/** Raw ticket shape from GET /kds/stations/{id}/tickets (no firstSeenAt yet). */
export interface TicketDto {
  orderId: string;
  fireBatchId: string;
  lines: TicketLine[];
}

/** Payload of the SignalR "OrderFired" event (station group). */
export interface OrderFiredEvent {
  fireBatchId: string;
  orderId: string;
  lines: Array<{
    id: string;
    itemId: string;
    sequence: number;
    quantity: number;
    unitPrice: number;
    modifiers: TicketLineModifier[];
  }>;
}

/** Payload of the SignalR "OrderLineVoided" event (station group). */
export interface OrderLineVoidedEvent {
  orderId: string;
  lineId: string;
  reason?: string;
}
