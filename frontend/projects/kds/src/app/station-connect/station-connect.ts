import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { SessionService } from 'shared';

/**
 * Dev/config entry for a KDS terminal: pick which station this screen shows and
 * the tenant/branch context it connects with. KimooKit login replaces this in a
 * later slice — the SessionService interface stays the same.
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
    const stationId = this.stationId().trim();
    if (!this.tenantId().trim() || !this.branchId().trim() || !stationId || !this.userId().trim()) {
      this.error.set('Tenant, branch, station, and user id are all required.');
      return;
    }
    this.busy.set(true);
    try {
      await this.session.connect({
        tenantId: this.tenantId().trim(),
        branchId: this.branchId().trim(),
        userId: this.userId().trim(),
        role: 'Manager',
      });
      await this.router.navigate(['/board', stationId]);
    } catch {
      this.error.set('Could not reach the backend. Is the API running on :5299?');
    } finally {
      this.busy.set(false);
    }
  }
}
