import { Injectable, inject, signal } from '@angular/core';
import { Subject } from 'rxjs';
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { API_CONFIG } from '../config/api-config';
import { SessionService } from '../auth/session.service';
import {
  OrderFiredEvent,
  OrderLineStatusChangedEvent,
  OrderLineVoidedEvent,
} from '../domain/models';

export type ConnectionStatus = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

/**
 * Thin wrapper over the /posHub SignalR connection.
 *
 * Exposes each station event as an RxJS stream. On every (re)connection it
 * re-joins the station group and pushes a `reconnected$` tick so the board can
 * re-fetch the authoritative ticket list and reconcile any events missed while
 * offline (SignalR does not replay past a drop).
 */
@Injectable({ providedIn: 'root' })
export class PosHubService {
  private readonly api = inject(API_CONFIG);
  private readonly session = inject(SessionService);

  private connection?: HubConnection;
  private joinedStationId?: string;

  readonly orderFired$ = new Subject<OrderFiredEvent>();
  readonly orderLineVoided$ = new Subject<OrderLineVoidedEvent>();
  readonly orderLineStatusChanged$ = new Subject<OrderLineStatusChangedEvent>();
  readonly reconnected$ = new Subject<void>();

  readonly status = signal<ConnectionStatus>('disconnected');

  /** Connect (if needed) and join a station group. Idempotent per station. */
  async connectToStation(stationId: string): Promise<void> {
    this.joinedStationId = stationId;

    if (!this.connection) {
      this.connection = new HubConnectionBuilder()
        .withUrl(`${this.api.baseUrl}/posHub`, {
          accessTokenFactory: () => this.session.token() ?? '',
        })
        .withAutomaticReconnect()
        .configureLogging(LogLevel.Warning)
        .build();

      this.connection.on('OrderFired', (e: OrderFiredEvent) => this.orderFired$.next(e));
      this.connection.on('OrderLineVoided', (e: OrderLineVoidedEvent) =>
        this.orderLineVoided$.next(e),
      );
      this.connection.on('OrderLineStatusChanged', (e: OrderLineStatusChangedEvent) =>
        this.orderLineStatusChanged$.next(e),
      );

      this.connection.onreconnecting(() => this.status.set('reconnecting'));
      this.connection.onreconnected(async () => {
        await this.joinGroup();
        this.status.set('connected');
        this.reconnected$.next();
      });
      this.connection.onclose(() => this.status.set('disconnected'));
    }

    if (this.connection.state === HubConnectionState.Disconnected) {
      this.status.set('connecting');
      await this.connection.start();
    }
    await this.joinGroup();
    this.status.set('connected');
  }

  async disconnect(): Promise<void> {
    this.joinedStationId = undefined;
    if (this.connection) {
      await this.connection.stop();
      this.connection = undefined;
    }
    this.status.set('disconnected');
  }

  private async joinGroup(): Promise<void> {
    if (this.connection && this.joinedStationId) {
      await this.connection.invoke('JoinStationGroup', this.joinedStationId);
    }
  }
}
