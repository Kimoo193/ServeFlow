import { Component, inject, OnInit, signal } from '@angular/core';
import { ReactiveFormsModule, FormGroup, FormControl, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { SessionService } from 'shared';
import { ManagerApiService, StaffUser, RolePermissionsResponse } from '../manager-api.service';

@Component({
  selector: 'app-staff',
  imports: [ReactiveFormsModule],
  templateUrl: './staff.html',
  styleUrl: './staff.css',
})
export class Staff implements OnInit {
  private readonly api = inject(ManagerApiService);
  private readonly session = inject(SessionService);
  private readonly router = inject(Router);

  readonly users = signal<StaffUser[]>([]);
  readonly rolePermissions = signal<RolePermissionsResponse | null>(null);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);

  readonly creatingUser = signal(false);
  readonly editingUserId = signal<string | null>(null);
  readonly confirmingUserId = signal<string | null>(null);

  readonly activeTab = signal<'directory' | 'matrix'>('directory');

  /* ── User form ── */
  readonly userForm = new FormGroup({
    displayName: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    role: new FormControl('Waiter', { nonNullable: true, validators: [Validators.required] }),
    isActive: new FormControl(true, { nonNullable: true }),
  });

  /* ── Permissions matrix form ── */
  readonly matrixForm = new FormGroup({});

  ngOnInit(): void {
    if (!this.session.isConnected()) {
      this.router.navigate(['/']);
      return;
    }
    this.loadAll();
  }

  private async loadAll(): Promise<void> {
    try {
      const [users, rp] = await Promise.all([
        this.api.getUsers(),
        this.api.getRolePermissions(),
      ]);
      this.users.set(users);
      this.rolePermissions.set(rp);
      this.buildMatrixForm();
      this.error.set(null);
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Failed to load staff data'));
    }
  }

  /* ── User CRUD ── */

  startCreate(): void {
    this.creatingUser.set(true);
    this.editingUserId.set(null);
    this.userForm.reset({ displayName: '', role: 'Waiter', isActive: true });
  }

  cancelCreate(): void {
    this.creatingUser.set(false);
  }

  async saveCreate(): Promise<void> {
    if (this.userForm.invalid) return;
    this.busy.set(true);
    try {
      const { displayName, role } = this.userForm.getRawValue();
      await this.api.createUser({ displayName, role });
      this.creatingUser.set(false);
      await this.loadAll();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Create user failed'));
    } finally {
      this.busy.set(false);
    }
  }

  startEdit(user: StaffUser): void {
    this.editingUserId.set(user.id);
    this.creatingUser.set(false);
    this.userForm.setValue({
      displayName: user.displayName,
      role: user.role,
      isActive: user.isActive,
    });
  }

  cancelEdit(): void {
    this.editingUserId.set(null);
  }

  async saveEdit(): Promise<void> {
    if (this.userForm.invalid || !this.editingUserId()) return;
    this.busy.set(true);
    try {
      const { displayName, role, isActive } = this.userForm.getRawValue();
      await this.api.updateUser(this.editingUserId()!, { displayName, role, isActive });
      this.editingUserId.set(null);
      await this.loadAll();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Update user failed'));
    } finally {
      this.busy.set(false);
    }
  }

  requestDeactivate(id: string): void {
    this.confirmingUserId.set(id);
  }

  cancelDeactivate(): void {
    this.confirmingUserId.set(null);
  }

  async confirmDeactivate(): Promise<void> {
    const id = this.confirmingUserId();
    if (!id) return;
    this.busy.set(true);
    try {
      const user = this.users().find(u => u.id === id);
      if (!user) return;
      await this.api.updateUser(id, { displayName: user.displayName, role: user.role, isActive: false });
      this.confirmingUserId.set(null);
      await this.loadAll();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Deactivate user failed'));
    } finally {
      this.busy.set(false);
    }
  }

  /* ── Permissions matrix ── */

  private buildMatrixForm(): void {
    const rp = this.rolePermissions();
    if (!rp) return;

    for (const role of rp.roles) {
      for (const perm of rp.permissions) {
        const key = this.matrixKey(role, perm);
        const checked = rp.grants.find(g => g.role === role)?.permissions.includes(perm) ?? false;
        if (!this.matrixForm.contains(key)) {
          this.matrixForm.addControl(key, new FormControl(checked, { nonNullable: true }));
        } else {
          this.matrixForm.get(key)?.setValue(checked, { emitEvent: false });
        }
      }
    }
  }

  matrixKey(role: string, permission: string): string {
    return `${role}:${permission}`;
  }

  async saveRolePermissions(role: string): Promise<void> {
    const rp = this.rolePermissions();
    if (!rp) return;

    const selected: string[] = [];
    for (const perm of rp.permissions) {
      const key = this.matrixKey(role, perm);
      if (this.matrixForm.get(key)?.value) {
        selected.push(perm);
      }
    }

    this.busy.set(true);
    try {
      await this.api.setRolePermissions(role, selected);
      const newRp = await this.api.getRolePermissions();
      this.rolePermissions.set(newRp);
      this.buildMatrixForm();
      this.error.set(null);
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Save permissions failed'));
    } finally {
      this.busy.set(false);
    }
  }

  /* ── Utilities ── */

  closeError(): void { this.error.set(null); }

  private extractError(e: unknown, fallback: string): string {
    const body = (e as Record<string, unknown>)?.['error'] as Record<string, unknown> | undefined;
    return (body?.['title'] as string) || (body?.['detail'] as string) || (e as Error)?.message || fallback;
  }
}
