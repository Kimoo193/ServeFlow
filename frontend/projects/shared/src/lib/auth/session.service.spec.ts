import { TestBed } from '@angular/core/testing';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { API_CONFIG } from '../config/api-config';
import {
  APP_ID,
  SessionService,
  loginErrorMessage,
} from './session.service';

function base64url(input: string): string {
  return btoa(input).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function jwtWithExp(exp: number): string {
  const header = base64url(JSON.stringify({ alg: 'HS256' }));
  const body = base64url(JSON.stringify({ exp }));
  return `${header}.${body}.fakesignature`;
}

const API = { baseUrl: 'http://localhost:5299' };
const APP = 'testapp';
const KEY = 'sf.testapp.session';

const LOGIN_REQUEST = {
  tenantId: 'tenant-1',
  branchId: 'branch-1',
  username: 'waiter1',
  password: 'secret123',
};

// A real, decodable JWT: the token login persists is the same token restore() later
// has to accept, so a placeholder string here would make the two halves of the suite
// disagree about what a stored session looks like.
const ACCESS_TOKEN = jwtWithExp(Math.floor(Date.now() / 1000) + 8 * 3600);

const LOGIN_RESPONSE = {
  accessToken: ACCESS_TOKEN,
  expiresAt: '2026-12-31T23:59:59Z',
  role: 'Waiter',
  userId: 'user-abc',
};

describe('SessionService', () => {
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: API_CONFIG, useValue: API },
        { provide: APP_ID, useValue: APP },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpTesting.verify();
    localStorage.clear();
  });

  describe('login()', () => {
    let service: SessionService;

    beforeEach(() => {
      service = TestBed.inject(SessionService);
    });

    it('POSTs tenantId, branchId, username and password to the /auth/login endpoint', async () => {
      const promise = service.login(LOGIN_REQUEST);

      const req = httpTesting.expectOne('http://localhost:5299/auth/login');
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({
        tenantId: 'tenant-1',
        branchId: 'branch-1',
        username: 'waiter1',
        password: 'secret123',
      });

      req.flush(LOGIN_RESPONSE);
      await promise;
    });

    it('persists the access token under the APP_ID-scoped key', async () => {
      const promise = service.login(LOGIN_REQUEST);
      httpTesting.expectOne('http://localhost:5299/auth/login').flush(LOGIN_RESPONSE);
      await promise;

      expect(service.isConnected()).toBe(true);
      expect(service.token()).toBe(ACCESS_TOKEN);

      const raw = localStorage.getItem(KEY);
      expect(raw).not.toBeNull();
      const parsed = JSON.parse(raw!);
      expect(parsed.token).toBe(ACCESS_TOKEN);
    });

    it('stores userId and role from the server response, not from the login request', async () => {
      const promise = service.login(LOGIN_REQUEST);
      httpTesting.expectOne('http://localhost:5299/auth/login').flush(LOGIN_RESPONSE);
      await promise;

      const ctx = service.context()!;
      expect(ctx.userId).toBe('user-abc');
      expect(ctx.role).toBe('Waiter');
    });

    it('stores tenantId and branchId from the login request', async () => {
      const promise = service.login(LOGIN_REQUEST);
      httpTesting.expectOne('http://localhost:5299/auth/login').flush(LOGIN_RESPONSE);
      await promise;

      const ctx = service.context()!;
      expect(ctx.tenantId).toBe('tenant-1');
      expect(ctx.branchId).toBe('branch-1');
    });

    it('never persists the password', async () => {
      const promise = service.login(LOGIN_REQUEST);
      httpTesting.expectOne('http://localhost:5299/auth/login').flush(LOGIN_RESPONSE);
      await promise;

      const raw = localStorage.getItem(KEY)!;
      expect(raw).not.toContain('secret123');
    });

    it('ensures two apps with different APP_ID values do not share sessions', () => {
      const futureExp = Math.floor(Date.now() / 1000) + 3600;
      const kdsSession = {
        token: jwtWithExp(futureExp),
        context: { tenantId: 't', branchId: 'b', userId: 'u', role: 'Waiter' },
      };
      localStorage.setItem('sf.kds.session', JSON.stringify(kdsSession));

      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          provideHttpClient(),
          provideHttpClientTesting(),
          { provide: API_CONFIG, useValue: API },
          { provide: APP_ID, useValue: 'cashier' },
        ],
      });
      const cashier = TestBed.inject(SessionService);
      httpTesting = TestBed.inject(HttpTestingController);

      expect(cashier.isConnected()).toBe(false);
      expect(localStorage.getItem('sf.kds.session')).not.toBeNull();
    });
  });

  describe('restore() via constructor', () => {
    it('restores a session whose JWT exp is in the future', () => {
      const futureExp = Math.floor(Date.now() / 1000) + 3600;
      const session = {
        token: jwtWithExp(futureExp),
        context: { tenantId: 't', branchId: 'b', userId: 'u', role: 'Chef' },
      };
      localStorage.setItem(KEY, JSON.stringify(session));

      const svc = TestBed.inject(SessionService);

      expect(svc.isConnected()).toBe(true);
      expect(svc.token()).toBe(session.token);
      expect(svc.context()!.userId).toBe('u');
      expect(svc.context()!.role).toBe('Chef');
    });

    it('discards a session whose JWT exp is in the past', () => {
      const pastExp = Math.floor(Date.now() / 1000) - 3600;
      const session = {
        token: jwtWithExp(pastExp),
        context: { tenantId: 't', branchId: 'b', userId: 'u', role: 'Waiter' },
      };
      localStorage.setItem(KEY, JSON.stringify(session));

      const svc = TestBed.inject(SessionService);

      expect(svc.isConnected()).toBe(false);
      expect(localStorage.getItem(KEY)).toBeNull();
    });

    it('discards a value that is not a structurally valid JWT', () => {
      localStorage.setItem(
        KEY,
        JSON.stringify({
          token: 'not-a-jwt',
          context: { tenantId: 't', branchId: 'b', userId: 'u', role: 'Waiter' },
        }),
      );

      const svc = TestBed.inject(SessionService);

      expect(svc.isConnected()).toBe(false);
      expect(localStorage.getItem(KEY)).toBeNull();
    });

    it('discards a session with a token whose payload is not JSON', () => {
      const garbledToken = `header.${base64url('not-json')}.sig`;
      localStorage.setItem(
        KEY,
        JSON.stringify({
          token: garbledToken,
          context: { tenantId: 't', branchId: 'b', userId: 'u', role: 'Waiter' },
        }),
      );

      const svc = TestBed.inject(SessionService);

      expect(svc.isConnected()).toBe(false);
      expect(localStorage.getItem(KEY)).toBeNull();
    });

    it('discards a session whose context is missing', () => {
      const futureExp = Math.floor(Date.now() / 1000) + 3600;
      localStorage.setItem(
        KEY,
        JSON.stringify({ token: jwtWithExp(futureExp) }),
      );

      const svc = TestBed.inject(SessionService);

      expect(svc.isConnected()).toBe(false);
      expect(localStorage.getItem(KEY)).toBeNull();
    });

    it('discards malformed JSON and removes the entry', () => {
      localStorage.setItem(KEY, 'this-is-not-json');

      const svc = TestBed.inject(SessionService);

      expect(svc.isConnected()).toBe(false);
      expect(localStorage.getItem(KEY)).toBeNull();
    });

    it('stays disconnected when storage is empty', () => {
      const svc = TestBed.inject(SessionService);

      expect(svc.isConnected()).toBe(false);
    });
  });

  describe('login() then reload', () => {
    it('keeps the operator signed in when the terminal is refreshed', async () => {
      const service = TestBed.inject(SessionService);
      const promise = service.login(LOGIN_REQUEST);
      httpTesting.expectOne('http://localhost:5299/auth/login').flush(LOGIN_RESPONSE);
      await promise;

      // A refresh is a fresh service over the same storage — the case every KDS and
      // till hits daily, and the only one that exercises login and restore together.
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          provideHttpClient(),
          provideHttpClientTesting(),
          { provide: API_CONFIG, useValue: API },
          { provide: APP_ID, useValue: APP },
        ],
      });
      httpTesting = TestBed.inject(HttpTestingController);
      const reloaded = TestBed.inject(SessionService);

      expect(reloaded.isConnected()).toBe(true);
      expect(reloaded.token()).toBe(ACCESS_TOKEN);
      expect(reloaded.context()!.role).toBe('Waiter');
      expect(reloaded.context()!.userId).toBe('user-abc');
    });
  });

  describe('disconnect()', () => {
    let service: SessionService;

    beforeEach(async () => {
      service = TestBed.inject(SessionService);
      const promise = service.login(LOGIN_REQUEST);
      httpTesting.expectOne('http://localhost:5299/auth/login').flush(LOGIN_RESPONSE);
      await promise;
    });

    it('clears token, context and localStorage entry', () => {
      expect(service.isConnected()).toBe(true);

      service.disconnect();

      expect(service.isConnected()).toBe(false);
      expect(service.token()).toBeNull();
      expect(service.context()).toBeNull();
      expect(localStorage.getItem(KEY)).toBeNull();
    });
  });
});

describe('loginErrorMessage', () => {
  it('returns a network error message for status 0', () => {
    const error = new HttpErrorResponse({ status: 0 });
    expect(loginErrorMessage(error)).toBe(
      'Could not reach the backend. Is the API running?',
    );
  });

  it('returns the server detail string for a 400 that provides one', () => {
    const error = new HttpErrorResponse({
      status: 400,
      error: { detail: 'Tenant not found' },
    });
    expect(loginErrorMessage(error)).toBe('Tenant not found');
  });

  it('returns a generic validation message for a 400 without a detail string', () => {
    expect(loginErrorMessage(new HttpErrorResponse({ status: 400, error: {} }))).toBe(
      'Check the tenant and branch ids.',
    );
    expect(
      loginErrorMessage(
        new HttpErrorResponse({ status: 400, error: { detail: 123 } }),
      ),
    ).toBe('Check the tenant and branch ids.');
  });

  it('returns an auth-failure message for status 401', () => {
    const error = new HttpErrorResponse({ status: 401 });
    expect(loginErrorMessage(error)).toBe('Invalid username or password.');
  });

  it('returns a rate-limiting message for status 429', () => {
    const error = new HttpErrorResponse({ status: 429 });
    expect(loginErrorMessage(error)).toBe(
      'Too many sign-in attempts. Wait a minute and try again.',
    );
  });

  it('returns a generic message for an unmapped error status', () => {
    const error = new HttpErrorResponse({ status: 500 });
    expect(loginErrorMessage(error)).toBe(
      'Could not sign in. Please try again.',
    );
  });

  it('returns a generic message for a value that is not an HttpErrorResponse', () => {
    expect(loginErrorMessage(new Error('network'))).toBe(
      'Could not sign in. Please try again.',
    );
    expect(loginErrorMessage('some string')).toBe(
      'Could not sign in. Please try again.',
    );
    expect(loginErrorMessage(null)).toBe(
      'Could not sign in. Please try again.',
    );
    expect(loginErrorMessage(undefined)).toBe(
      'Could not sign in. Please try again.',
    );
  });
});
