import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { SessionService, loginErrorMessage } from 'shared';

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
  readonly username = signal('');
  readonly password = signal('');
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);

  constructor() {
    // The till is provisioned with its tenant/branch; only credentials are
    // re-entered. Never pre-fill a password.
    const ctx = this.session.context();
    if (ctx) {
      this.tenantId.set(ctx.tenantId);
      this.branchId.set(ctx.branchId);
    }
  }

  async connect(): Promise<void> {
    this.error.set(null);
    if (!this.tenantId().trim() || !this.branchId().trim()) {
      this.error.set('Tenant and branch id are both required.');
      return;
    }
    if (!this.username().trim() || !this.password()) {
      this.error.set('Username and password are required.');
      return;
    }
    this.busy.set(true);
    try {
      await this.session.login({
        tenantId: this.tenantId().trim(),
        branchId: this.branchId().trim(),
        username: this.username().trim(),
        password: this.password(),
      });
      this.password.set('');
      await this.router.navigate(['/orders']);
    } catch (err) {
      this.error.set(loginErrorMessage(err));
    } finally {
      this.busy.set(false);
    }
  }
}
