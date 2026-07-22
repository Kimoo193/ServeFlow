import {
  Component,
  DestroyRef,
  OnDestroy,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import {
  OrderFiredEvent,
  OrderLineVoidedEvent,
  PosHubService,
  SessionService,
  Ticket,
  TicketLine,
} from 'shared';
import { KdsApiService } from '../kds-api.service';
import { TicketCard } from '../ticket-card/ticket-card';

const NEW_PULSE_MS = 1200;
const VOID_FLASH_MS = 1400;

/**
 * The KDS station board. Loads the authoritative ticket list, then keeps it live
 * from SignalR: new fire batches slide in, voids flash then leave, and Start/Ready
 * bump lines through their lifecycle. One screen per station.
 */
@Component({
  selector: 'app-station-board',
  imports: [TicketCard],
  templateUrl: './station-board.html',
  styleUrl: './station-board.css',
})
export class StationBoard implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(KdsApiService);
  private readonly hub = inject(PosHubService);
  private readonly session = inject(SessionService);
  private readonly destroyRef = inject(DestroyRef);

  stationId = '';
  readonly stationName = signal('');
  readonly tickets = signal<Ticket[]>([]);
  readonly itemNames = signal<Map<string, string>>(new Map());
  readonly now = signal(Date.now());
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly connection = this.hub.status;

  private clockTimer?: ReturnType<typeof setInterval>;

  async ngOnInit(): Promise<void> {
    if (!this.session.isConnected()) {
      void this.router.navigate(['/']);
      return;
    }
    this.stationId = this.route.snapshot.paramMap.get('stationId') ?? '';

    this.hub.orderFired$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((e) => this.onFired(e));
    this.hub.orderLineVoided$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((e) => this.onVoided(e));
    this.hub.reconnected$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => void this.reload());

    this.clockTimer = setInterval(() => this.now.set(Date.now()), 1000);

    await this.reload();
    try {
      await this.hub.connectToStation(this.stationId);
    } catch {
      this.error.set('Live updates unavailable — could not reach the realtime hub.');
    }
  }

  ngOnDestroy(): void {
    if (this.clockTimer) clearInterval(this.clockTimer);
    void this.hub.disconnect();
  }

  leave(): void {
    void this.router.navigate(['/']);
  }

  /** (Re)load the authoritative board, preserving ages of tickets we already know. */
  async reload(): Promise<void> {
    this.loading.set(true);
    try {
      const [name, menu, dtos] = await Promise.all([
        this.api.getStationName(this.stationId),
        this.api.getMenuItems(),
        this.api.getTickets(this.stationId),
      ]);
      this.stationName.set(name);
      this.itemNames.set(new Map(menu.map((m) => [m.id, m.name])));

      const knownAge = new Map(
        this.tickets().map((t) => [`${t.orderId}:${t.fireBatchId}`, t.firstSeenAt]),
      );
      const seen = Date.now();
      this.tickets.set(
        dtos.map((d) => ({
          ...d,
          firstSeenAt: knownAge.get(`${d.orderId}:${d.fireBatchId}`) ?? seen,
        })),
      );
      this.error.set(null);
    } catch {
      this.error.set('Could not load the station board.');
    } finally {
      this.loading.set(false);
    }
  }

  // --- realtime handlers -------------------------------------------------

  private onFired(e: OrderFiredEvent): void {
    const incoming: TicketLine[] = e.lines.map((l) => ({
      id: l.id,
      itemId: l.itemId,
      sequence: l.sequence,
      quantity: l.quantity,
      unitPrice: l.unitPrice,
      status: 'Fired',
      modifiers: l.modifiers ?? [],
    }));

    this.tickets.update((tickets) => {
      const key = `${e.orderId}:${e.fireBatchId}`;
      const existing = tickets.find((t) => `${t.orderId}:${t.fireBatchId}` === key);
      if (existing) {
        const have = new Set(existing.lines.map((l) => l.id));
        const merged = [...existing.lines, ...incoming.filter((l) => !have.has(l.id))];
        return tickets.map((t) =>
          t === existing ? { ...t, lines: sortLines(merged) } : t,
        );
      }
      const ticket: Ticket = {
        orderId: e.orderId,
        fireBatchId: e.fireBatchId,
        lines: sortLines(incoming),
        firstSeenAt: Date.now(),
        isNew: true,
      };
      window.setTimeout(() => this.clearNewFlag(key), NEW_PULSE_MS);
      return [...tickets, ticket];
    });
  }

  private clearNewFlag(key: string): void {
    this.tickets.update((tickets) =>
      tickets.map((t) =>
        `${t.orderId}:${t.fireBatchId}` === key ? { ...t, isNew: false } : t,
      ),
    );
  }

  private onVoided(e: OrderLineVoidedEvent): void {
    this.tickets.update((tickets) =>
      tickets.map((t) => ({
        ...t,
        lines: t.lines.map((l) =>
          l.id === e.lineId ? { ...l, voiding: true } : l,
        ),
      })),
    );
    window.setTimeout(() => this.removeLine(e.lineId), VOID_FLASH_MS);
  }

  // --- bump actions ------------------------------------------------------

  async onStart(line: TicketLine): Promise<void> {
    this.setLineStatus(line.id, 'Preparing');
    try {
      await this.api.startPreparing(line.id);
    } catch {
      this.setLineStatus(line.id, 'Fired');
      this.error.set('Could not start that line — it may have changed. Refreshing.');
      await this.reload();
    }
  }

  async onReady(line: TicketLine): Promise<void> {
    // Ready leaves the active board — remove optimistically, restore on failure.
    this.removeLine(line.id);
    try {
      await this.api.markReady(line.id);
    } catch {
      this.error.set('Could not mark ready — it may have changed. Refreshing.');
      await this.reload();
    }
  }

  // --- helpers -----------------------------------------------------------

  private setLineStatus(lineId: string, status: TicketLine['status']): void {
    this.tickets.update((tickets) =>
      tickets.map((t) => ({
        ...t,
        lines: t.lines.map((l) => (l.id === lineId ? { ...l, status } : l)),
      })),
    );
  }

  private removeLine(lineId: string): void {
    this.tickets.update((tickets) =>
      tickets
        .map((t) => ({ ...t, lines: t.lines.filter((l) => l.id !== lineId) }))
        .filter((t) => t.lines.length > 0),
    );
  }
}

function sortLines(lines: TicketLine[]): TicketLine[] {
  return [...lines].sort((a, b) => a.sequence - b.sequence);
}
