import { signal, Signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { SessionContext, SessionService } from 'shared';
import { StationConnect } from './station-connect';

describe('StationConnect', () => {
  let login: ReturnType<typeof vi.fn>;
  let contextSignal: ReturnType<typeof signal<SessionContext | null>>;
  let navigate: ReturnType<typeof vi.fn>;

  function createComponent(): StationConnect {
    return TestBed.createComponent(StationConnect).componentInstance;
  }

  beforeEach(() => {
    login = vi.fn().mockResolvedValue(undefined);
    contextSignal = signal<SessionContext | null>(null);
    navigate = vi.fn().mockResolvedValue(true);

    TestBed.configureTestingModule({
      providers: [
        { provide: SessionService, useValue: { login, context: contextSignal } },
        { provide: Router, useValue: { navigate } },
      ],
    });
  });

  describe('constructor', () => {
    it('pre-fills tenant and branch from an existing session context', () => {
      contextSignal.set({ tenantId: 't-restaurant', branchId: 'b-kitchen', userId: 'u1', role: 'Kitchen' });
      const component = createComponent();

      expect(component.tenantId()).toBe('t-restaurant');
      expect(component.branchId()).toBe('b-kitchen');
      expect(component.password()).toBe('');
    });

    it('never pre-fills a password', () => {
      contextSignal.set({ tenantId: 't1', branchId: 'b1', userId: 'u1', role: 'Waiter' });
      const component = createComponent();

      component.tenantId.set('tenant-x');
      component.branchId.set('branch-y');
      component.password.set('secret-in-memory');

      expect(component.password()).toBe('secret-in-memory');
      // password was set programmatically, not from context — the constructor
      // should never touch the password signal
      contextSignal.set(null);
      const fresh = createComponent();
      expect(fresh.password()).toBe('');
    });

    it('leaves fields empty when no prior session exists', () => {
      const component = createComponent();

      expect(component.tenantId()).toBe('');
      expect(component.branchId()).toBe('');
      expect(component.stationId()).toBe('');
      expect(component.username()).toBe('');
      expect(component.password()).toBe('');
    });
  });

  describe('connect()', () => {
    it('sets error when tenant is missing', async () => {
      const component = createComponent();
      component.branchId.set('b1');
      component.stationId.set('grill');
      component.username.set('waiter');
      component.password.set('pass');

      await component.connect();

      expect(component.error()).toBe('Tenant, branch, and station id are all required.');
      expect(login).not.toHaveBeenCalled();
    });

    it('sets error when branch is missing', async () => {
      const component = createComponent();
      component.tenantId.set('t1');
      component.stationId.set('grill');
      component.username.set('waiter');
      component.password.set('pass');

      await component.connect();

      expect(component.error()).toBe('Tenant, branch, and station id are all required.');
      expect(login).not.toHaveBeenCalled();
    });

    it('sets error when station id is missing', async () => {
      const component = createComponent();
      component.tenantId.set('t1');
      component.branchId.set('b1');
      component.username.set('waiter');
      component.password.set('pass');

      await component.connect();

      expect(component.error()).toBe('Tenant, branch, and station id are all required.');
      expect(login).not.toHaveBeenCalled();
    });

    it('sets error when username is missing', async () => {
      const component = createComponent();
      component.tenantId.set('t1');
      component.branchId.set('b1');
      component.stationId.set('grill');
      component.password.set('pass');

      await component.connect();

      expect(component.error()).toBe('Username and password are required.');
      expect(login).not.toHaveBeenCalled();
    });

    it('sets error when password is missing', async () => {
      const component = createComponent();
      component.tenantId.set('t1');
      component.branchId.set('b1');
      component.stationId.set('grill');
      component.username.set('waiter');

      await component.connect();

      expect(component.error()).toBe('Username and password are required.');
      expect(login).not.toHaveBeenCalled();
    });

    it('calls login with trimmed fields and raw password, then navigates to /board/:stationId', async () => {
      const component = createComponent();
      component.tenantId.set('  t-resto  ');
      component.branchId.set('  b-main  ');
      component.stationId.set('grill');
      component.username.set('  chef1  ');
      component.password.set('s3cret!');

      await component.connect();

      expect(login).toHaveBeenCalledWith({
        tenantId: 't-resto',
        branchId: 'b-main',
        username: 'chef1',
        password: 's3cret!',
      });
      expect(navigate).toHaveBeenCalledWith(['/board', 'grill']);
    });

    it('clears the password signal after a successful login', async () => {
      const component = createComponent();
      component.tenantId.set('t1');
      component.branchId.set('b1');
      component.stationId.set('grill');
      component.username.set('chef');
      component.password.set('keep-me-secret');

      await component.connect();

      expect(component.password()).toBe('');
    });

    it('sets error from loginErrorMessage on failure and does not navigate', async () => {
      login.mockRejectedValueOnce(new HttpErrorResponse({ status: 401 }));
      const component = createComponent();
      component.tenantId.set('t1');
      component.branchId.set('b1');
      component.stationId.set('grill');
      component.username.set('chef');
      component.password.set('wrong');

      await component.connect();

      expect(component.error()).toBe('Invalid username or password.');
      expect(navigate).not.toHaveBeenCalled();
    });
  });
});
