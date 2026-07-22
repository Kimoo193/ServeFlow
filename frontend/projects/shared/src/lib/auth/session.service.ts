import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { API_CONFIG } from '../config/api-config';

/** The tenant/branch/user context a KDS terminal connects with. */
export interface SessionContext {
  tenantId: string;
  branchId: string;
  userId: string;
  role: string;
}

interface DevTokenResponse {
  accessToken: string;
}

const STORAGE_KEY = 'sf.kds.session';

/**
 * Holds the signed JWT and its context for the current terminal.
 *
 * Phase 1 dev mode uses POST /dev/token; KimooKit login replaces this later
 * behind the same interface. The token and context are persisted so a KDS
 * screen survives a refresh without re-entering ids.
 */
@Injectable({ providedIn: 'root' })
export class SessionService {
  private readonly http = inject(HttpClient);
  private readonly api = inject(API_CONFIG);

  private readonly _token = signal<string | null>(null);
  private readonly _context = signal<SessionContext | null>(null);

  readonly token = this._token.asReadonly();
  readonly context = this._context.asReadonly();
  readonly isConnected = computed(() => this._token() !== null);

  constructor() {
    this.restore();
  }

  /** Exchange a context for a signed dev token and remember it. */
  async connect(context: SessionContext): Promise<void> {
    const res = await firstValueFrom(
      this.http.post<DevTokenResponse>(`${this.api.baseUrl}/dev/token`, context),
    );
    this._token.set(res.accessToken);
    this._context.set(context);
    localStorage.setItem(
      STORAGE_KEY,
      JSON.stringify({ token: res.accessToken, context }),
    );
  }

  disconnect(): void {
    this._token.set(null);
    this._context.set(null);
    localStorage.removeItem(STORAGE_KEY);
  }

  private restore(): void {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return;
    try {
      const parsed = JSON.parse(raw) as { token: string; context: SessionContext };
      this._token.set(parsed.token);
      this._context.set(parsed.context);
    } catch {
      localStorage.removeItem(STORAGE_KEY);
    }
  }
}
