import { Component, EventEmitter, inject, Input, Output, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ManagerApiService, MenuTree, MenuCombo } from '../../manager-api.service';

@Component({
  selector: 'app-combo-panel',
  imports: [ReactiveFormsModule],
  templateUrl: './combo-panel.html',
  styleUrl: './combo-panel.css',
})
export class ComboPanel {
  private readonly api = inject(ManagerApiService);

  @Input({ required: true }) menuTree: MenuTree | null = null;
  @Output() refresh = new EventEmitter<void>();

  readonly editingComboId = signal<string | null>(null);
  readonly creatingCombo = signal(false);
  readonly confirming = signal<{ type: 'combo' | 'item'; id: string } | null>(null);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);

  readonly comboForm = new FormGroup({
    name: new FormControl('', Validators.required),
    price: new FormControl(0, [Validators.required, Validators.min(0)]),
  });

  readonly attachItemComboId = signal<string | null>(null);
  readonly attachItemSelect = signal('');
  readonly attachItemQuantity = signal(1);

  private itemMap = new Map<string, { name: string; categoryName: string }>();

  combos(): MenuCombo[] {
    return this.menuTree?.combos ?? [];
  }

  itemName(itemId: string): string {
    return this.itemMap.get(itemId)?.name ?? itemId;
  }

  allItems(): { id: string; name: string; categoryName: string }[] {
    const items: { id: string; name: string; categoryName: string }[] = [];
    if (!this.menuTree) return items;
    for (const cat of this.menuTree.categories) {
      for (const item of cat.items) {
        items.push({ id: item.id, name: item.name, categoryName: cat.name });
        this.itemMap.set(item.id, { name: item.name, categoryName: cat.name });
      }
    }
    return items;
  }

  availableItemsForCombo(combo: MenuCombo): { id: string; name: string; categoryName: string }[] {
    this.allItems();
    const attached = new Set(combo.items.map(i => i.itemId));
    return this.allItems().filter(i => !attached.has(i.id));
  }

  /* ── Combo CRUD ── */

  startCreateCombo(): void {
    this.creatingCombo.set(true);
    this.editingComboId.set(null);
    this.comboForm.reset({ name: '', price: 0 });
  }

  startEditCombo(c: MenuCombo): void {
    this.creatingCombo.set(false);
    this.editingComboId.set(c.id);
    this.comboForm.setValue({ name: c.name, price: c.price });
  }

  cancelComboEdit(): void {
    this.creatingCombo.set(false);
    this.editingComboId.set(null);
  }

  async saveCombo(): Promise<void> {
    if (this.comboForm.invalid) return;
    const val = this.comboForm.value as { name: string; price: number };
    this.error.set(null);
    this.busy.set(true);
    try {
      if (this.editingComboId()) {
        await this.api.updateCombo(this.editingComboId()!, val);
      } else {
        await this.api.createCombo(val);
      }
      this.cancelComboEdit();
      this.refresh.emit();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Save combo failed'));
    } finally {
      this.busy.set(false);
    }
  }

  requestDeleteCombo(id: string): void {
    this.confirming.set({ type: 'combo', id });
  }

  async executeDeleteCombo(id: string): Promise<void> {
    this.error.set(null);
    this.busy.set(true);
    try {
      await this.api.deleteCombo(id);
      this.confirming.set(null);
      this.refresh.emit();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Delete combo failed'));
    } finally {
      this.busy.set(false);
    }
  }

  /* ── Combo ⇄ Item links ── */

  startAttachItem(comboId: string): void {
    this.attachItemComboId.set(comboId);
    this.attachItemSelect.set('');
    this.attachItemQuantity.set(1);
  }

  cancelAttachItem(): void {
    this.attachItemComboId.set(null);
  }

  onAttachItemSelect(target: EventTarget | null): void {
    this.attachItemSelect.set((target as HTMLSelectElement)?.value ?? '');
  }

  onAttachItemQuantity(target: EventTarget | null): void {
    const v = parseInt((target as HTMLInputElement)?.value ?? '1', 10);
    this.attachItemQuantity.set(isNaN(v) || v < 1 ? 1 : v);
  }

  async attachItem(comboId: string): Promise<void> {
    const itemId = this.attachItemSelect();
    const quantity = this.attachItemQuantity();
    if (!itemId || quantity < 1) return;
    this.error.set(null);
    this.busy.set(true);
    try {
      await this.api.attachComboItem(comboId, itemId, quantity);
      this.cancelAttachItem();
      this.refresh.emit();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Attach item failed'));
    } finally {
      this.busy.set(false);
    }
  }

  requestDeleteItem(comboId: string, itemId: string): void {
    this.confirming.set({ type: 'item', id: comboId + ':' + itemId });
  }

  async executeDeleteItem(comboId: string, itemId: string): Promise<void> {
    this.error.set(null);
    this.busy.set(true);
    try {
      await this.api.detachComboItem(comboId, itemId);
      this.confirming.set(null);
      this.refresh.emit();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Detach item failed'));
    } finally {
      this.busy.set(false);
    }
  }

  isConfirmingItem(comboId: string, itemId: string): boolean {
    const c = this.confirming();
    return c?.type === 'item' && c.id === comboId + ':' + itemId;
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
