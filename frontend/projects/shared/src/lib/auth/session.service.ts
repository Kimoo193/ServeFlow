import { Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { API_CONFIG } from '../config/api-config';

/**
 * Identifies the app holding the session, e.g. `kds`. Provided per app so the
 * three front-ends get distinct storage keys — deployed on one host they would
 * otherwise share a key and silently overwrite each other's sessions.
 */
export const APP_ID = new InjectionToken<string>('sf.app-id');

/** What the operator types on a connect screen. */
export interface LoginRequest {
  tenantId: string;
  branchId: string;
  username: string;
  password: string;
}

/**
 * Who the signed-in operator is. `userId` and `role` come from the server's
 * login response — the client never asserts its own role.
 */
export interface SessionContext {
  tenantId: string;
  branchId: string;
  userId: string;
  role: string;
}

interface LoginResponse {
  accessToken: string;
  expiresAt: string;
  role: string;
  userId: string;
}

interface PersistedSession {
  token: string;
  context: SessionContext;
}

/**
 * Holds the signed JWT and its context for the current terminal.
 *
 * Authenticates against `POST /auth/login`; a KimooKit or OIDC provider can
 * replace that endpoint behind the same contract without touching this class.
 * The token and context are persisted so a terminal survives a refresh without
 * re-entering credentials.
 */
@Injectable({ providedIn: 'root' })
export class SessionService {
  private readonly http = inject(HttpClient);
  private readonly api = inject(API_CONFIG);
  private readonly storageKey = `sf.${inject(APP_ID)}.session`;

  private readonly _token = signal<string | null>(null);
  private readonly _context = signal<SessionContext | null>(null);

  readonly token = this._token.asReadonly();
  readonly context = this._context.asReadonly();
  readonly isConnected = computed(() => this._token() !== null);

  constructor() {
    this.restore();
  }

  /** Exchange credentials for a signed token and remember the session. */
  async login(request: LoginRequest): Promise<void> {
    const res = await firstValueFrom(
      this.http.post<LoginResponse>(`${this.api.baseUrl}/auth/login`, request),
    );
    const session: PersistedSession = {
      token: res.accessToken,
      context: {
        tenantId: request.tenantId,
        branchId: request.branchId,
        userId: res.userId,
        role: res.role,
      },
    };
    this._token.set(session.token);
    this._context.set(session.context);
    localStorage.setItem(this.storageKey, JSON.stringify(session));
  }

  disconnect(): void {
    this._token.set(null);
    this._context.set(null);
    localStorage.removeItem(this.storageKey);
  }

  private restore(): void {
    const raw = localStorage.getItem(this.storageKey);
    if (!raw) return;
    try {
      const parsed = JSON.parse(raw) as PersistedSession;
      // An expired token is worse than none: the app would render its shell,
      // fire its initial loads, and only discover the problem as a wall of 401s.
      // Anything we cannot read an `exp` out of is not a token we minted.
      const expiresAt = jwtExpiryMs(parsed.token);
      if (!parsed.context || expiresAt === null || expiresAt <= Date.now()) {
        localStorage.removeItem(this.storageKey);
        return;
      }
      this._token.set(parsed.token);
      this._context.set(parsed.context);
    } catch {
      localStorage.removeItem(this.storageKey);
    }
  }
}

/** `exp` from a JWT payload as epoch milliseconds, or null if unreadable. */
function jwtExpiryMs(token: string | undefined): number | null {
  const payload = token?.split('.')[1];
  if (!payload) return null;
  try {
    const b64 = payload.replace(/-/g, '+').replace(/_/g, '/');
    const padded = b64 + '='.repeat((4 - (b64.length % 4)) % 4);
    const bytes = Uint8Array.from(atob(padded), (c) => c.charCodeAt(0));
    const claims = JSON.parse(new TextDecoder().decode(bytes)) as { exp?: unknown };
    return typeof claims.exp === 'number' ? claims.exp * 1000 : null;
  } catch {
    return null;
  }
}

/**
 * Turns a failed login into something a person at a terminal can act on.
 * Shared by all three connect screens so they stay consistent.
 */
export function loginErrorMessage(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) {
    return 'Could not sign in. Please try again.';
  }
  switch (error.status) {
    case 0:
      return 'Could not reach the backend. Is the API running?';
    case 400:
      return typeof error.error?.detail === 'string'
        ? error.error.detail
        : 'Check the tenant and branch ids.';
    case 401:
      return 'Invalid username or password.';
    case 429:
      return 'Too many sign-in attempts. Wait a minute and try again.';
    default:
      return 'Could not sign in. Please try again.';
  }
}
