import { Component, inject, OnInit, signal } from '@angular/core';
import { Router } from '@angular/router';
import { SessionService } from 'shared';
import { ManagerApiService, DashboardSummary } from '../manager-api.service';

@Component({
  selector: 'app-dashboard',
  imports: [],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.css',
})
export class Dashboard implements OnInit {
  private readonly api = inject(ManagerApiService);
  private readonly session = inject(SessionService);
  private readonly router = inject(Router);

  readonly summary = signal<DashboardSummary | null>(null);
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
      this.summary.set(await this.api.getDashboardSummary());
      this.error.set(null);
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Failed to load dashboard'));
    }
  }

  closeError(): void {
    this.error.set(null);
  }

  private extractError(e: unknown, fallback: string): string {
    const body = (e as Record<string, unknown>)?.['error'] as Record<string, unknown> | undefined;
    return (body?.['title'] as string) || (body?.['detail'] as string) || (e as Error)?.message || fallback;
  }
}
