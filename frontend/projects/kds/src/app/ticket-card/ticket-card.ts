import { Component, computed, input, output } from '@angular/core';
import {
  OrderLineStatus,
  STATUS_COLOR_VAR,
  Ticket,
  TicketLine,
} from 'shared';

/** Heat thresholds for the aging timer, in seconds. */
const WARM_AFTER = 5 * 60;
const HOT_AFTER = 10 * 60;

/**
 * The signature element: a ticket with a status-coloured left spine and a live
 * aging timer that heats calm → amber → red. Everything else stays quiet.
 */
@Component({
  selector: 'app-ticket-card',
  imports: [],
  templateUrl: './ticket-card.html',
  styleUrl: './ticket-card.css',
})
export class TicketCard {
  readonly ticket = input.required<Ticket>();
  readonly names = input.required<Map<string, string>>();
  readonly now = input.required<number>(); // epoch ms, ticks every second

  readonly start = output<TicketLine>();
  readonly ready = output<TicketLine>();

  /** The ticket's overall state = its least-advanced line (what the kitchen owes). */
  readonly status = computed<OrderLineStatus>(() => {
    const lines = this.ticket().lines;
    return lines.some((l) => l.status === 'Fired') ? 'Fired' : 'Preparing';
  });

  readonly spineColor = computed(() => STATUS_COLOR_VAR[this.status()]);

  readonly ageSeconds = computed(() =>
    Math.max(0, Math.floor((this.now() - this.ticket().firstSeenAt) / 1000)),
  );

  readonly heat = computed<'calm' | 'warm' | 'hot'>(() => {
    const s = this.ageSeconds();
    return s >= HOT_AFTER ? 'hot' : s >= WARM_AFTER ? 'warm' : 'calm';
  });

  readonly clock = computed(() => {
    const s = this.ageSeconds();
    const mm = Math.floor(s / 60).toString().padStart(2, '0');
    const ss = (s % 60).toString().padStart(2, '0');
    return `${mm}:${ss}`;
  });

  shortOrder = computed(() => this.ticket().orderId.slice(0, 4).toUpperCase());
  shortBatch = computed(() => this.ticket().fireBatchId.slice(0, 4).toUpperCase());

  itemName(line: TicketLine): string {
    return this.names().get(line.itemId) ?? `Item ${line.itemId.slice(0, 4)}`;
  }
}
