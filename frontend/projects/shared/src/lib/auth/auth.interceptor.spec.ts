import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { API_CONFIG } from '../config/api-config';
import { APP_ID, SessionService } from './session.service';
import { authInterceptor, authErrorInterceptor } from './auth.interceptor';

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

const SESSION = (token: string) => ({
  token,
  context: { tenantId: 't', branchId: 'b', userId: 'u', role: 'Waiter' },
});

describe('authInterceptor', () => {
  let httpClient: HttpClient;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: API_CONFIG, useValue: API },
        { provide: APP_ID, useValue: APP },
      ],
    });
    httpClient = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpTesting.verify();
    localStorage.clear();
  });

  it('attaches the Bearer token when a session exists', () => {
    const futureExp = Math.floor(Date.now() / 1000) + 3600;
    const token = jwtWithExp(futureExp);
    localStorage.setItem(KEY, JSON.stringify(SESSION(token)));

    httpClient.get('/api/tables').subscribe({ next: vi.fn() });
    const req = httpTesting.expectOne('/api/tables');

    expect(req.request.headers.get('Authorization')).toBe(`Bearer ${token}`);
    req.flush({});
  });

  it('does not attach an Authorization header when there is no session', () => {
    httpClient.get('/api/tables').subscribe({ next: vi.fn() });
    const req = httpTesting.expectOne('/api/tables');

    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({});
  });
});

describe('authErrorInterceptor', () => {
  let httpClient: HttpClient;
  let httpTesting: HttpTestingController;
  let navigateSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    localStorage.clear();
    navigateSpy = vi.fn().mockResolvedValue(true);
    TestBed.configureTestingModule({
      providers: [
        { provide: Router, useValue: { navigate: navigateSpy } },
        provideHttpClient(withInterceptors([authErrorInterceptor])),
        provideHttpClientTesting(),
        { provide: API_CONFIG, useValue: API },
        { provide: APP_ID, useValue: APP },
      ],
    });
    httpClient = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpTesting.verify();
    localStorage.clear();
  });

  function primeSession(token?: string): SessionService {
    const t = token ?? jwtWithExp(Math.floor(Date.now() / 1000) + 3600);
    localStorage.setItem(KEY, JSON.stringify(SESSION(t)));
    return TestBed.inject(SessionService);
  }

  it('clears the session and navigates to / on a 401 from a non-login endpoint while a session exists', () => {
    const session = primeSession();
    expect(session.isConnected()).toBe(true);

    let capturedError: unknown = null;
    httpClient.get('/api/orders').subscribe({ error: (err) => { capturedError = err; } });
    httpTesting.expectOne('/api/orders').flush('', { status: 401, statusText: 'Unauthorized' });

    expect(capturedError).toBeInstanceOf(HttpErrorResponse);
    expect((capturedError as HttpErrorResponse).status).toBe(401);
    expect(session.isConnected()).toBe(false);
    expect(navigateSpy).toHaveBeenCalledWith(['/']);
  });

  it('does not clear the session or navigate when a 401 comes from the /auth/login endpoint', () => {
    const session = primeSession();
    expect(session.isConnected()).toBe(true);

    let capturedError: unknown = null;
    httpClient.get('/auth/login').subscribe({ error: (err) => { capturedError = err; } });
    httpTesting.expectOne('/auth/login').flush('', { status: 401, statusText: 'Unauthorized' });

    expect(capturedError).toBeInstanceOf(HttpErrorResponse);
    expect((capturedError as HttpErrorResponse).status).toBe(401);
    expect(session.isConnected()).toBe(true);
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it('does not navigate on a 401 when no session exists', () => {
    let capturedError: unknown = null;
    httpClient.get('/api/orders').subscribe({ error: (err) => { capturedError = err; } });
    httpTesting.expectOne('/api/orders').flush('', { status: 401, statusText: 'Unauthorized' });

    expect(capturedError).toBeInstanceOf(HttpErrorResponse);
    expect((capturedError as HttpErrorResponse).status).toBe(401);
    expect(navigateSpy).not.toHaveBeenCalled();

    const session = TestBed.inject(SessionService);
    expect(session.isConnected()).toBe(false);
  });

  it('passes through a non-401 error unchanged without touching the session', () => {
    const session = primeSession();
    expect(session.isConnected()).toBe(true);

    let capturedError: unknown = null;
    httpClient.get('/api/orders').subscribe({ error: (err) => { capturedError = err; } });
    httpTesting.expectOne('/api/orders').flush('', { status: 500, statusText: 'Server Error' });

    expect(capturedError).toBeInstanceOf(HttpErrorResponse);
    expect((capturedError as HttpErrorResponse).status).toBe(500);
    expect(session.isConnected()).toBe(true);
    expect(navigateSpy).not.toHaveBeenCalled();
  });
});
