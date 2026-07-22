import { DecimalPipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { SessionService } from 'shared';
import {
  CashierApiService,
  BillPreview,
  Receipt,
  MenuItem,
} from '../cashier-api.service';

@Component({
  selector: 'app-order-review',
  imports: [RouterLink, DecimalPipe],
  templateUrl: './order-review.html',
  styleUrl: './order-review.css',
})
export class OrderReview implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly session = inject(SessionService);
  private readonly api = inject(CashierApiService);

  readonly orderId = signal('');
  readonly preview = signal<BillPreview | null>(null);
  readonly receipt = signal<Receipt | null>(null);
  readonly itemNames = signal<Map<string, string>>(new Map());
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly closeError = signal<string | null>(null);
  readonly closing = signal(false);
  readonly printing = signal(false);
  readonly printResult = signal<string | null>(null);

  /** 'preview' = reviewing the open bill; 'closed' = showing the snapshotted receipt. */
  readonly viewState = signal<'preview' | 'closed'>('preview');

  ngOnInit(): void {
    if (!this.session.isConnected()) {
      void this.router.navigate(['/']);
      return;
    }
    this.orderId.set(this.route.snapshot.paramMap.get('id') ?? '');
    if (!this.orderId()) {
      void this.router.navigate(['/orders']);
      return;
    }
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    this.closeError.set(null);
    this.printResult.set(null);

    try {
      const [menu, preview] = await Promise.all([
        this.api.getMenuItems().catch(() => [] as MenuItem[]),
        this.api.getBillPreview(this.orderId()),
      ]);
      this.itemNames.set(new Map(menu.map((m) => [m.id, m.name])));
      this.preview.set(preview);
      this.viewState.set('preview');
    } catch (err: unknown) {
      const status = (err as { status?: number }).status;
      if (status === 409) {
        // Order is already closed — fetch receipt directly
        await this.loadReceipt();
      } else {
        this.error.set('Could not load the bill.');
      }
    } finally {
      this.loading.set(false);
    }
  }

  async close(): Promise<void> {
    const p = this.preview();
    if (!p || !p.canClose) return;

    this.closing.set(true);
    this.closeError.set(null);
    try {
      await this.api.closeOrder(this.orderId(), p.rowVersion);
      await this.loadReceipt();
    } catch (err: unknown) {
      const status = (err as { status?: number }).status;
      if (status === 409) {
        this.closeError.set('Order changed or still cooking — refresh the preview.');
        await this.load();
      } else {
        this.closeError.set('Could not close the order. Please try again.');
      }
    } finally {
      this.closing.set(false);
    }
  }

  async print(): Promise<void> {
    this.printing.set(true);
    this.printResult.set(null);
    try {
      await this.api.printReceipt(this.orderId());
      this.printResult.set('Sent to printer');
    } catch {
      this.printResult.set('Printer unreachable — check the station');
    } finally {
      this.printing.set(false);
    }
  }

  private async loadReceipt(): Promise<void> {
    this.error.set(null);
    try {
      const [menu, r] = await Promise.all([
        this.api.getMenuItems().catch(() => [] as MenuItem[]),
        this.api.getReceipt(this.orderId()),
      ]);
      this.itemNames.set(new Map(menu.map((m) => [m.id, m.name])));
      this.receipt.set(r);
      this.viewState.set('closed');
    } catch {
      this.error.set('Could not load the receipt.');
    }
  }

  itemName(itemId: string): string {
    return this.itemNames().get(itemId) ?? `Item ${itemId.slice(0, 4)}`;
  }

  trackLine(_: number, l: { id: string }): string {
    return l.id;
  }

  trackMod(_: number, m: { name: string }): string {
    return m.name;
  }
}
