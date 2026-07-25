import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { SessionService, loginErrorMessage } from 'shared';

/**
 * Entry for a KDS terminal: pick which station this screen shows and sign in as
 * the kitchen account that will bump its tickets.
 *
 * The role is whatever the signed-in user actually is — a wall screen no longer
 * asserts `Manager` for itself. A `Kitchen` account carries exactly
 * `BumpKitchenLine`, which is all this board needs.
 */
@Component({
  selector: 'app-station-connect',
  imports: [FormsModule],
  templateUrl: './station-connect.html',
  styleUrl: './station-connect.css',
})
export class StationConnect {
  private readonly session = inject(SessionService);
  private readonly router = inject(Router);

  readonly tenantId = signal('');
  readonly branchId = signal('');
  readonly stationId = signal('');
  readonly username = signal('');
  readonly password = signal('');
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);

  constructor() {
    // The terminal is provisioned with its tenant/branch; only credentials are
    // re-entered. Never pre-fill a password.
    const ctx = this.session.context();
    if (ctx) {
      this.tenantId.set(ctx.tenantId);
      this.branchId.set(ctx.branchId);
    }
  }

  async connect(): Promise<void> {
    this.error.set(null);
    const stationId = this.stationId().trim();
    if (!this.tenantId().trim() || !this.branchId().trim() || !stationId) {
      this.error.set('Tenant, branch, and station id are all required.');
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
      await this.router.navigate(['/board', stationId]);
    } catch (err) {
      this.error.set(loginErrorMessage(err));
    } finally {
      this.busy.set(false);
    }
  }
}
