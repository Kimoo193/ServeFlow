import { Component, EventEmitter, inject, OnInit, Output, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ManagerApiService, ModifierGroupDto, ModifierDto } from '../../manager-api.service';

@Component({
  selector: 'app-modifier-library',
  imports: [ReactiveFormsModule],
  templateUrl: './modifier-library.html',
  styleUrl: './modifier-library.css',
})
export class ModifierLibrary implements OnInit {
  private readonly api = inject(ManagerApiService);

  @Output() refresh = new EventEmitter<void>();

  readonly groups = signal<ModifierGroupDto[]>([]);
  readonly modifiers = signal<ModifierDto[]>([]);
  readonly editingGroupId = signal<string | null>(null);
  readonly creatingGroup = signal(false);
  readonly editingModifierId = signal<string | null>(null);
  readonly creatingModifierGroupId = signal<string | null>(null);
  readonly confirming = signal<{ type: 'group' | 'modifier'; id: string } | null>(null);
  readonly expandedGroupId = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);

  readonly groupForm = new FormGroup({
    name: new FormControl('', Validators.required),
    minSelect: new FormControl(0, [Validators.required, Validators.min(0)]),
    maxSelect: new FormControl(0, [Validators.required, Validators.min(0)]),
    required: new FormControl(false),
  });

  readonly modifierForm = new FormGroup({
    name: new FormControl('', Validators.required),
    priceDelta: new FormControl(0, Validators.required),
  });

  ngOnInit(): void {
    this.loadAll();
  }

  private async loadAll(): Promise<void> {
    try {
      const [groups, modifiers] = await Promise.all([
        this.api.getModifierGroups(),
        this.api.getModifiers(),
      ]);
      this.groups.set(groups);
      this.modifiers.set(modifiers);
      this.error.set(null);
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Failed to load modifier data'));
    }
  }

  modifiersForGroup(groupId: string): ModifierDto[] {
    return this.modifiers().filter(m => m.groupId === groupId);
  }

  toggleExpand(groupId: string): void {
    this.expandedGroupId.set(this.expandedGroupId() === groupId ? null : groupId);
  }

  /* ── Group CRUD ── */

  startCreateGroup(): void {
    this.creatingGroup.set(true);
    this.editingGroupId.set(null);
    this.groupForm.reset({ name: '', minSelect: 0, maxSelect: 0, required: false });
  }

  startEditGroup(g: ModifierGroupDto): void {
    this.creatingGroup.set(false);
    this.editingGroupId.set(g.id);
    this.groupForm.setValue({ name: g.name, minSelect: g.minSelect, maxSelect: g.maxSelect, required: g.required });
  }

  cancelGroupEdit(): void {
    this.creatingGroup.set(false);
    this.editingGroupId.set(null);
  }

  async saveGroup(): Promise<void> {
    if (this.groupForm.invalid) return;
    const val = this.groupForm.value as { name: string; minSelect: number; maxSelect: number; required: boolean };
    if (val.maxSelect < val.minSelect) {
      this.error.set('Max select must be ≥ min select');
      return;
    }
    this.error.set(null);
    this.busy.set(true);
    try {
      if (this.editingGroupId()) {
        await this.api.updateModifierGroup(this.editingGroupId()!, val);
      } else {
        await this.api.createModifierGroup(val);
      }
      this.cancelGroupEdit();
      await this.loadAll();
      this.refresh.emit();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Save group failed'));
    } finally {
      this.busy.set(false);
    }
  }

  requestDeleteGroup(id: string): void {
    this.confirming.set({ type: 'group', id });
  }

  async executeDeleteGroup(id: string): Promise<void> {
    this.error.set(null);
    this.busy.set(true);
    try {
      await this.api.deleteModifierGroup(id);
      this.confirming.set(null);
      await this.loadAll();
      this.refresh.emit();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Delete group failed'));
    } finally {
      this.busy.set(false);
    }
  }

  /* ── Modifier CRUD ── */

  startCreateModifier(groupId: string): void {
    this.creatingModifierGroupId.set(groupId);
    this.editingModifierId.set(null);
    this.expandedGroupId.set(groupId);
    this.modifierForm.reset({ name: '', priceDelta: 0 });
  }

  startEditModifier(m: ModifierDto): void {
    this.creatingModifierGroupId.set(null);
    this.editingModifierId.set(m.id);
    this.expandedGroupId.set(m.groupId);
    this.modifierForm.setValue({ name: m.name, priceDelta: m.priceDelta });
  }

  cancelModifierEdit(): void {
    this.creatingModifierGroupId.set(null);
    this.editingModifierId.set(null);
  }

  async saveModifier(groupId: string): Promise<void> {
    if (this.modifierForm.invalid) return;
    const val = this.modifierForm.value as { name: string; priceDelta: number };
    this.error.set(null);
    this.busy.set(true);
    try {
      if (this.editingModifierId()) {
        await this.api.updateModifier(this.editingModifierId()!, { groupId, ...val });
      } else {
        await this.api.createModifier({ groupId, ...val });
      }
      this.cancelModifierEdit();
      await this.loadAll();
      this.refresh.emit();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Save modifier failed'));
    } finally {
      this.busy.set(false);
    }
  }

  requestDeleteModifier(id: string): void {
    this.confirming.set({ type: 'modifier', id });
  }

  async executeDeleteModifier(id: string): Promise<void> {
    this.error.set(null);
    this.busy.set(true);
    try {
      await this.api.deleteModifier(id);
      this.confirming.set(null);
      await this.loadAll();
      this.refresh.emit();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Delete modifier failed'));
    } finally {
      this.busy.set(false);
    }
  }

  cancelConfirm(): void {
    this.confirming.set(null);
  }

  closeError(): void {
    this.error.set(null);
  }

  private extractError(e: unknown, fallback: string): string {
    const body = (e as Record<string, unknown>)?.['error'] as Record<string, unknown> | undefined;
    return (body?.['title'] as string) || (body?.['detail'] as string) || (e as Error)?.message || fallback;
  }
}
