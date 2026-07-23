import { Component, inject, OnInit, signal } from '@angular/core';
import { Router } from '@angular/router';
import { SessionService } from 'shared';
import { ManagerApiService, OrderHistoryRow } from '../manager-api.service';

@Component({
  selector: 'app-history',
  imports: [],
  templateUrl: './history.html',
  styleUrl: './history.css',
})
export class OrderHistory implements OnInit {
  private readonly api = inject(ManagerApiService);
  private readonly session = inject(SessionService);
  private readonly router = inject(Router);

  readonly orders = signal<OrderHistoryRow[]>([]);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    if (!this.session.isConnected()) {
      this.router.navigate(['/']);
      return;
    }
    this.load();
  }

  private async load(): Promise<void> {
    try {
      this.orders.set(await this.api.getOrderHistory());
      this.error.set(null);
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Failed to load order history'));
    }
  }

  closeError(): void {
    this.error.set(null);
  }

  formatTime(iso: string): string {
    const d = new Date(iso);
    return d.toLocaleString(undefined, {
      year: 'numeric', month: 'short', day: 'numeric',
      hour: '2-digit', minute: '2-digit',
    });
  }

  formatMoney(value: number | null): string {
    if (value === null) return '—';
    return `${value.toFixed(2)}`;
  }

  private extractError(e: unknown, fallback: string): string {
    const body = (e as Record<string, unknown>)?.['error'] as Record<string, unknown> | undefined;
    return (body?.['title'] as string) || (body?.['detail'] as string) || (e as Error)?.message || fallback;
  }
}
