import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { Subject } from 'rxjs';
import {
  ConnectionStatus,
  OrderFiredEvent,
  OrderLineStatusChangedEvent,
  OrderLineVoidedEvent,
  PosHubService,
  SessionService,
  Ticket,
  TicketDto,
  TicketLine,
} from 'shared';
import { KdsApiService } from '../kds-api.service';
import { StationBoard } from './station-board';

const LINE_A: TicketLine = {
  id: 'line-a', itemId: 'item-1', sequence: 1, quantity: 2, unitPrice: 9.99,
  status: 'Fired', modifiers: [],
};
const LINE_B: TicketLine = {
  id: 'line-b', itemId: 'item-2', sequence: 2, quantity: 1, unitPrice: 5.50,
  status: 'Fired', modifiers: [],
};
const LINE_C: TicketLine = {
  id: 'line-c', itemId: 'item-3', sequence: 1, quantity: 1, unitPrice: 7.99,
  status: 'Fired', modifiers: [],
};

const TICKET_1_DTO: TicketDto = {
  orderId: 'order-1', fireBatchId: 'batch-1', lines: [LINE_A, LINE_B],
};
const TICKET_2_DTO: TicketDto = {
  orderId: 'order-2', fireBatchId: 'batch-2', lines: [LINE_C],
};

function firedEvent(overrides: {
  orderId: string; fireBatchId: string;
  lines: OrderFiredEvent['lines'];
}): OrderFiredEvent {
  return { orderId: overrides.orderId, fireBatchId: overrides.fireBatchId, lines: overrides.lines };
}

const MENU_ITEMS = [
  { id: 'item-1', categoryId: 'cat-1', stationId: 'station-1', name: 'Burger', price: 9.99, availability: 'Available' as const },
  { id: 'item-2', categoryId: 'cat-1', stationId: 'station-1', name: 'Fries', price: 5.50, availability: 'Available' as const },
  { id: 'item-3', categoryId: 'cat-2', stationId: 'station-1', name: 'Salad', price: 7.99, availability: 'Available' as const },
];

describe('StationBoard', () => {
  let orderFired$: Subject<OrderFiredEvent>;
  let orderLineVoided$: Subject<OrderLineVoidedEvent>;
  let orderLineStatusChanged$: Subject<OrderLineStatusChangedEvent>;
  let reconnected$: Subject<void>;
  let hubStatus: ReturnType<typeof signal<ConnectionStatus>>;
  let connectToStation: ReturnType<typeof vi.fn>;
  let disconnect: ReturnType<typeof vi.fn>;

  let getTickets: ReturnType<typeof vi.fn>;
  let getMenuItems: ReturnType<typeof vi.fn>;
  let getStationName: ReturnType<typeof vi.fn>;
  let startPreparing: ReturnType<typeof vi.fn>;
  let markReady: ReturnType<typeof vi.fn>;
  let navigate: ReturnType<typeof vi.fn>;
  let paramGet: ReturnType<typeof vi.fn>;
  let isConnected: ReturnType<typeof signal<boolean>>;

  function createComponent(): StationBoard {
    return TestBed.createComponent(StationBoard).componentInstance;
  }

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-07-01T00:00:00Z'));

    orderFired$ = new Subject<OrderFiredEvent>();
    orderLineVoided$ = new Subject<OrderLineVoidedEvent>();
    orderLineStatusChanged$ = new Subject<OrderLineStatusChangedEvent>();
    reconnected$ = new Subject<void>();
    hubStatus = signal<ConnectionStatus>('disconnected');
    connectToStation = vi.fn().mockResolvedValue(undefined);
    disconnect = vi.fn().mockResolvedValue(undefined);

    getTickets = vi.fn().mockResolvedValue([]);
    getMenuItems = vi.fn().mockResolvedValue([]);
    getStationName = vi.fn().mockResolvedValue('Station');
    startPreparing = vi.fn().mockResolvedValue({ status: 'Preparing' });
    markReady = vi.fn().mockResolvedValue({ status: 'Ready' });
    navigate = vi.fn().mockResolvedValue(true);
    paramGet = vi.fn().mockReturnValue('station-1');
    isConnected = signal(true);

    TestBed.configureTestingModule({
      providers: [
        { provide: KdsApiService, useValue: { getTickets, getMenuItems, getStationName, startPreparing, markReady } },
        { provide: PosHubService, useValue: { orderFired$, orderLineVoided$, orderLineStatusChanged$, reconnected$, status: hubStatus, connectToStation, disconnect } },
        { provide: Router, useValue: { navigate } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: paramGet } } } },
        { provide: SessionService, useValue: { isConnected } },
      ],
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  describe('ngOnInit when session is not connected', () => {
    it('redirects to / and does not load', async () => {
      isConnected.set(false);
      const component = createComponent();
      await component.ngOnInit();

      expect(navigate).toHaveBeenCalledWith(['/']);
      expect(getTickets).not.toHaveBeenCalled();
      expect(getStationName).not.toHaveBeenCalled();
    });
  });

  describe('reload()', () => {
    it('builds tickets from DTOs and resolves item names', async () => {
      getStationName.mockResolvedValue('Grill Station');
      getMenuItems.mockResolvedValue(MENU_ITEMS);
      getTickets.mockResolvedValue([TICKET_1_DTO]);
      const component = createComponent();

      await component.reload();

      expect(component.stationName()).toBe('Grill Station');
      expect(component.loading()).toBe(false);
      expect(component.error()).toBeNull();

      const names = component.itemNames();
      expect(names.get('item-1')).toBe('Burger');
      expect(names.get('item-2')).toBe('Fries');

      const tickets = component.tickets();
      expect(tickets.length).toBe(1);
      expect(tickets[0].orderId).toBe('order-1');
      expect(tickets[0].fireBatchId).toBe('batch-1');
      expect(tickets[0].lines.length).toBe(2);
      expect(tickets[0].firstSeenAt).toBe(Date.now());
    });

    it('signals loading state and sets error on API failure', async () => {
      getTickets.mockRejectedValue(new Error('network'));
      const component = createComponent();

      await component.reload();

      expect(component.error()).toBe('Could not load the station board.');
      expect(component.loading()).toBe(false);
    });

    it('passes station id from the route to API calls', async () => {
      getTickets.mockResolvedValue([]);
      const component = createComponent();
      component.stationId = 'grill-42';

      await component.reload();

      expect(getStationName).toHaveBeenCalledWith('grill-42');
      expect(getTickets).toHaveBeenCalledWith('grill-42');
    });
  });

  describe('age preservation on reload()', () => {
    it('preserves firstSeenAt for tickets already on the board', async () => {
      vi.setSystemTime(new Date('2026-07-01T00:00:00Z'));
      const earlierTime = new Date('2026-06-30T12:00:00Z').getTime();

      const existingTicket: Ticket = {
        orderId: 'order-1', fireBatchId: 'batch-1',
        lines: [LINE_A],
        firstSeenAt: earlierTime,
      };

      getTickets.mockResolvedValue([TICKET_1_DTO]);
      const component = createComponent();
      component.tickets.set([existingTicket]);

      expect(component.tickets()[0].firstSeenAt).toBe(earlierTime);

      await component.reload();

      expect(component.tickets().length).toBe(1);
      expect(component.tickets()[0].firstSeenAt).toBe(earlierTime);
    });

    it('stamps Date.now() for genuinely new tickets', async () => {
      vi.setSystemTime(new Date('2026-07-01T00:01:00Z'));
      const now = Date.now();

      getTickets.mockResolvedValue([TICKET_1_DTO]);
      const component = createComponent();
      // no existing tickets

      await component.reload();

      expect(component.tickets()[0].firstSeenAt).toBe(now);
    });
  });

  describe('OrderFired event', () => {
    let component: StationBoard;

    beforeEach(async () => {
      getTickets.mockResolvedValue([]);
      component = createComponent();
      await component.ngOnInit();
    });

    it('appends a new ticket with isNew: true and lines sorted by sequence', () => {
      orderFired$.next(firedEvent({
        orderId: 'order-new',
        fireBatchId: 'batch-new',
        lines: [
          { id: 'line-z', itemId: 'item-1', sequence: 3, quantity: 1, unitPrice: 9.99, modifiers: [] },
          { id: 'line-a', itemId: 'item-2', sequence: 1, quantity: 2, unitPrice: 5.50, modifiers: [] },
        ],
      }));

      const tickets = component.tickets();
      expect(tickets.length).toBe(1);
      const ticket = tickets[0];
      expect(ticket.orderId).toBe('order-new');
      expect(ticket.fireBatchId).toBe('batch-new');
      expect(ticket.isNew).toBe(true);
      expect(ticket.lines.length).toBe(2);
      // sorted by sequence
      expect(ticket.lines[0].sequence).toBe(1);
      expect(ticket.lines[1].sequence).toBe(3);
      expect(ticket.lines[0].status).toBe('Fired');
    });

    it('merges new lines into an existing ticket, de-duplicating by line id', async () => {
      // pre-populate a ticket
      getTickets.mockResolvedValue([TICKET_1_DTO]);
      component = createComponent();
      await component.ngOnInit();

      // existing ticket has LINE_A (id: line-a) and LINE_B (id: line-b)
      expect(component.tickets()[0].lines.length).toBe(2);

      // fire to same (orderId, fireBatchId): one new line, one duplicate of line-a
      orderFired$.next(firedEvent({
        orderId: 'order-1',
        fireBatchId: 'batch-1',
        lines: [
          { id: 'line-c', itemId: 'item-3', sequence: 3, quantity: 1, unitPrice: 7.99, modifiers: [] },
          { id: 'line-a', itemId: 'item-1', sequence: 1, quantity: 2, unitPrice: 9.99, modifiers: [] }, // duplicate
        ],
      }));

      const tickets = component.tickets();
      expect(tickets.length).toBe(1);
      const merged = tickets[0].lines;
      expect(merged.length).toBe(3); // line-a, line-b, line-c — no duplicate

      const ids = merged.map((l) => l.id);
      expect(ids).toContain('line-a');
      expect(ids).toContain('line-b');
      expect(ids).toContain('line-c');

      // sorted by sequence
      for (let i = 1; i < merged.length; i++) {
        expect(merged[i].sequence).toBeGreaterThanOrEqual(merged[i - 1].sequence);
      }
    });

    it('clears the isNew flag after NEW_PULSE_MS (1200ms)', () => {
      orderFired$.next(firedEvent({
        orderId: 'order-new', fireBatchId: 'batch-new',
        lines: [{ id: 'line-x', itemId: 'item-1', sequence: 1, quantity: 1, unitPrice: 9.99, modifiers: [] }],
      }));

      expect(component.tickets()[0].isNew).toBe(true);

      vi.advanceTimersByTime(1199);
      expect(component.tickets()[0].isNew).toBe(true);

      vi.advanceTimersByTime(1);
      expect(component.tickets()[0].isNew).toBe(false);
    });
  });

  describe('OrderLineVoided event', () => {
    let component: StationBoard;

    beforeEach(async () => {
      getTickets.mockResolvedValue([TICKET_1_DTO]);
      component = createComponent();
      await component.ngOnInit();
    });

    it('marks the line voiding: true then removes it after VOID_FLASH_MS (1400ms)', () => {
      expect(component.tickets()[0].lines.length).toBe(2);

      orderLineVoided$.next({ orderId: 'order-1', lineId: 'line-a' });

      // flash phase: line is marked voiding but still present
      const linesFlashing = component.tickets()[0].lines;
      expect(linesFlashing.length).toBe(2);
      const voidingLine = linesFlashing.find((l) => l.id === 'line-a');
      expect(voidingLine!.voiding).toBe(true);

      vi.advanceTimersByTime(1400);

      // removal phase: line is gone
      const linesAfter = component.tickets()[0].lines;
      expect(linesAfter.length).toBe(1);
      expect(linesAfter[0].id).toBe('line-b');
    });

    it('removes the entire ticket when the last line is voided', () => {
      component.tickets.set([{
        orderId: 'solo', fireBatchId: 'b1',
        lines: [{ ...LINE_A, id: 'solo-line' }],
        firstSeenAt: Date.now(),
      }]);

      expect(component.tickets().length).toBe(1);

      orderLineVoided$.next({ orderId: 'solo', lineId: 'solo-line' });
      vi.advanceTimersByTime(1400);

      expect(component.tickets().length).toBe(0);
    });
  });

  describe('OrderLineStatusChanged event', () => {
    let component: StationBoard;

    beforeEach(async () => {
      getTickets.mockResolvedValue([TICKET_1_DTO]);
      component = createComponent();
      await component.ngOnInit();
    });

    it('removes the line when status is Ready', () => {
      expect(component.tickets()[0].lines.find((l) => l.id === 'line-a')).toBeTruthy();

      orderLineStatusChanged$.next({
        orderId: 'order-1', lineId: 'line-a', fireBatchId: 'batch-1', status: 'Ready',
      });

      expect(component.tickets()[0].lines.find((l) => l.id === 'line-a')).toBeUndefined();
      expect(component.tickets()[0].lines.length).toBe(1);
    });

    it('updates the line status in place for any non-Ready status', () => {
      orderLineStatusChanged$.next({
        orderId: 'order-1', lineId: 'line-a', fireBatchId: 'batch-1', status: 'Preparing',
      });

      const updated = component.tickets()[0].lines.find((l) => l.id === 'line-a');
      expect(updated!.status).toBe('Preparing');
    });

    it('removes the ticket when the last line is marked Ready', () => {
      component.tickets.set([{
        orderId: 'solo', fireBatchId: 'b1',
        lines: [{ ...LINE_A, id: 'solo-line' }],
        firstSeenAt: Date.now(),
      }]);
      expect(component.tickets().length).toBe(1);

      orderLineStatusChanged$.next({
        orderId: 'solo', lineId: 'solo-line', fireBatchId: 'b1', status: 'Ready',
      });

      expect(component.tickets().length).toBe(0);
    });
  });

  describe('onStart()', () => {
    let component: StationBoard;

    beforeEach(async () => {
      getTickets.mockResolvedValue([TICKET_1_DTO]);
      component = createComponent();
      await component.ngOnInit();
    });

    it('sets the line to Preparing immediately (optimistic)', () => {
      component.onStart(LINE_A);

      const updated = component.tickets()[0].lines.find((l) => l.id === 'line-a');
      expect(updated!.status).toBe('Preparing');
      expect(startPreparing).toHaveBeenCalledWith('line-a');
    });

    it('reverts status and reloads on API failure', async () => {
      startPreparing.mockRejectedValueOnce(new Error('fail'));
      getTickets.mockResolvedValue([{
        orderId: 'order-1', fireBatchId: 'batch-1',
        lines: [{ ...LINE_A, status: 'Fired' }, LINE_B],
      }]);

      await component.onStart(LINE_A);

      const reverted = component.tickets()[0].lines.find((l) => l.id === 'line-a');
      expect(reverted!.status).toBe('Fired');
      // reload was called (it re-fetched tickets)
      expect(getTickets).toHaveBeenCalledWith('station-1');
    });

    it('leaves the failure message on screen after the reload that follows it', async () => {
      startPreparing.mockRejectedValueOnce(new Error('fail'));

      await component.onStart(LINE_A);

      // The reload succeeds and clears error() on its way out, so the message has to be
      // set afterwards or the line just snaps back with no explanation.
      expect(component.error()).toBe('Could not start that line — it may have changed.');
    });

    it('keeps the more serious load failure when the reload also fails', async () => {
      startPreparing.mockRejectedValueOnce(new Error('fail'));
      getTickets.mockRejectedValue(new Error('offline'));

      await component.onStart(LINE_A);

      expect(component.error()).toBe('Could not load the station board.');
    });
  });

  describe('onReady()', () => {
    let component: StationBoard;

    beforeEach(async () => {
      getTickets.mockResolvedValue([TICKET_1_DTO]);
      component = createComponent();
      await component.ngOnInit();
    });

    it('removes the line immediately and calls the API', () => {
      expect(component.tickets()[0].lines.length).toBe(2);

      component.onReady(LINE_A);

      expect(component.tickets()[0].lines.length).toBe(1);
      expect(component.tickets()[0].lines[0].id).toBe('line-b');
      expect(markReady).toHaveBeenCalledWith('line-a');
    });

    it('reloads on API failure (reload restores the line)', async () => {
      markReady.mockRejectedValueOnce(new Error('fail'));
      getTickets.mockResolvedValue([TICKET_1_DTO]); // reload restores both lines

      await component.onReady(LINE_A);

      expect(getTickets).toHaveBeenCalledWith('station-1');
      // reload restored the line
      expect(component.tickets()[0].lines.length).toBe(2);
    });

    it('leaves the failure message on screen after the reload that follows it', async () => {
      markReady.mockRejectedValueOnce(new Error('fail'));

      await component.onReady(LINE_A);

      expect(component.error()).toBe('Could not mark ready — it may have changed.');
    });
  });

  describe('reconnected$', () => {
    it('triggers a reload', async () => {
      getTickets.mockResolvedValue([]);
      const component = createComponent();
      await component.ngOnInit();

      getTickets.mockClear();

      reconnected$.next();

      // flush the async reload promise chain
      await Promise.resolve();
      await Promise.resolve();

      expect(getTickets).toHaveBeenCalledWith('station-1');
    });
  });

  describe('ngOnDestroy', () => {
    it('clears the clock interval and disconnects the hub', async () => {
      getTickets.mockResolvedValue([]);
      const component = createComponent();
      await component.ngOnInit();

      const timerCountBefore = vi.getTimerCount();
      expect(timerCountBefore).toBeGreaterThan(0);

      component.ngOnDestroy();

      expect(disconnect).toHaveBeenCalled();
      expect(vi.getTimerCount()).toBeLessThan(timerCountBefore);
    });
  });
});
