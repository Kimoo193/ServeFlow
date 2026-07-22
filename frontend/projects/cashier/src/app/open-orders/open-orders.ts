import { Component, OnInit, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { SessionService } from 'shared';
import { CashierApiService, OpenOrderSummary } from '../cashier-api.service';

@Component({
  selector: 'app-open-orders',
  imports: [RouterLink],
  templateUrl: './open-orders.html',
  styleUrl: './open-orders.css',
})
export class OpenOrders implements OnInit {
  private readonly session = inject(SessionService);
  private readonly router = inject(Router);
  private readonly api = inject(CashierApiService);

  readonly orders = signal<OpenOrderSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    if (!this.session.isConnected()) {
      void this.router.navigate(['/']);
      return;
    }
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.orders.set(await this.api.getOpenOrders());
    } catch {
      this.error.set('Could not load open orders.');
    } finally {
      this.loading.set(false);
    }
  }

  trackById(_: number, o: OpenOrderSummary): string {
    return o.id;
  }
}
