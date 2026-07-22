import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { SessionService } from 'shared';

@Component({
  selector: 'app-cashier-connect',
  imports: [FormsModule],
  templateUrl: './cashier-connect.html',
  styleUrl: './cashier-connect.css',
})
export class CashierConnect {
  private readonly session = inject(SessionService);
  private readonly router = inject(Router);

  readonly tenantId = signal('');
  readonly branchId = signal('');
  readonly userId = signal('');
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);

  constructor() {
    const ctx = this.session.context();
    if (ctx) {
      this.tenantId.set(ctx.tenantId);
      this.branchId.set(ctx.branchId);
      this.userId.set(ctx.userId);
    }
  }

  async connect(): Promise<void> {
    this.error.set(null);
    if (!this.tenantId().trim() || !this.branchId().trim() || !this.userId().trim()) {
      this.error.set('Tenant, branch, and user id are all required.');
      return;
    }
    this.busy.set(true);
    try {
      await this.session.connect({
        tenantId: this.tenantId().trim(),
        branchId: this.branchId().trim(),
        userId: this.userId().trim(),
        role: 'Cashier',
      });
      await this.router.navigate(['/orders']);
    } catch {
      this.error.set('Could not reach the backend. Is the API running on :5299?');
    } finally {
      this.busy.set(false);
    }
  }
}
