import { Component, EventEmitter, inject, Input, OnInit, Output, signal } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ManagerApiService, MenuCategory, MenuItemDetail, MenuTree, Station, ModifierGroupDto } from '../../manager-api.service';

@Component({
  selector: 'app-category-panel',
  imports: [ReactiveFormsModule, CurrencyPipe],
  templateUrl: './category-panel.html',
  styleUrl: './category-panel.css',
})
export class CategoryPanel implements OnInit {
  private readonly api = inject(ManagerApiService);

  @Input({ required: true }) menuTree: MenuTree | null = null;
  @Input({ required: true }) stations: Station[] = [];
  @Output() refresh = new EventEmitter<void>();

  readonly allGroups = signal<ModifierGroupDto[]>([]);
  readonly editingCategoryId = signal<string | null>(null);
  readonly creatingCategory = signal(false);
  readonly editingItemId = signal<string | null>(null);
  readonly creatingItemCategoryId = signal<string | null>(null);
  readonly confirming = signal<{ type: 'category' | 'item'; id: string } | null>(null);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);

  readonly confirmAttach = signal<{ itemId: string; groupId: string } | null>(null);
  readonly attachingItemId = signal<string | null>(null);
  readonly attachGroupSelect = signal('');

  readonly categoryForm = new FormGroup({
    name: new FormControl('', Validators.required),
    sortOrder: new FormControl(0, [Validators.required, Validators.min(0)]),
  });

  readonly itemForm = new FormGroup({
    categoryId: new FormControl('', Validators.required),
    stationId: new FormControl('', Validators.required),
    name: new FormControl('', Validators.required),
    price: new FormControl(0, [Validators.required, Validators.min(0)]),
    availability: new FormControl('Available', Validators.required),
  });

  ngOnInit(): void {
    this.loadGroups();
  }

  private async loadGroups(): Promise<void> {
    try {
      this.allGroups.set(await this.api.getModifierGroups());
    } catch {
      this.error.set('Failed to load modifier groups');
    }
  }

  categories(): MenuCategory[] {
    return this.menuTree?.categories ?? [];
  }

  /* ── Category CRUD ── */

  startCreateCategory(): void {
    this.creatingCategory.set(true);
    this.editingCategoryId.set(null);
    this.categoryForm.reset({ name: '', sortOrder: 0 });
  }

  startEditCategory(cat: MenuCategory): void {
    this.creatingCategory.set(false);
    this.editingCategoryId.set(cat.id);
    this.categoryForm.setValue({ name: cat.name, sortOrder: cat.sortOrder });
  }

  cancelCategoryEdit(): void {
    this.creatingCategory.set(false);
    this.editingCategoryId.set(null);
  }

  async saveCategory(): Promise<void> {
    if (this.categoryForm.invalid) return;
    this.error.set(null);
    this.busy.set(true);
    try {
      const val = this.categoryForm.value as { name: string; sortOrder: number };
      if (this.editingCategoryId()) {
        await this.api.updateCategory(this.editingCategoryId()!, val);
      } else {
        await this.api.createCategory(val);
      }
      this.cancelCategoryEdit();
      this.refresh.emit();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Save category failed'));
    } finally {
      this.busy.set(false);
    }
  }

  requestDeleteCategory(id: string): void {
    this.confirming.set({ type: 'category', id });
  }

  async executeDeleteCategory(id: string): Promise<void> {
    this.error.set(null);
    this.busy.set(true);
    try {
      await this.api.deleteCategory(id);
      this.confirming.set(null);
      this.refresh.emit();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Delete category failed'));
    } finally {
      this.busy.set(false);
    }
  }

  /* ── Item CRUD ── */

  startCreateItem(categoryId: string): void {
    this.creatingItemCategoryId.set(categoryId);
    this.editingItemId.set(null);
    this.itemForm.setValue({
      categoryId,
      stationId: this.stations[0]?.id ?? '',
      name: '',
      price: 0,
      availability: 'Available',
    });
  }

  startEditItem(item: MenuItemDetail, categoryId: string): void {
    this.creatingItemCategoryId.set(null);
    this.editingItemId.set(item.id);
    this.itemForm.setValue({
      categoryId,
      stationId: item.stationId,
      name: item.name,
      price: item.price,
      availability: item.availability,
    });
  }

  cancelItemEdit(): void {
    this.creatingItemCategoryId.set(null);
    this.editingItemId.set(null);
    this.resetItemForm();
  }

  private resetItemForm(): void {
    this.itemForm.setValue({
      categoryId: '',
      stationId: '',
      name: '',
      price: 0,
      availability: 'Available',
    });
  }

  async saveItem(): Promise<void> {
    if (this.itemForm.invalid) return;
    this.error.set(null);
    this.busy.set(true);
    try {
      const val = this.itemForm.value as {
        categoryId: string; stationId: string;
        name: string; price: number; availability: string;
      };
      if (this.editingItemId()) {
        await this.api.updateMenuItem(this.editingItemId()!, val);
      } else {
        await this.api.createMenuItem(val);
      }
      this.cancelItemEdit();
      this.refresh.emit();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Save item failed'));
    } finally {
      this.busy.set(false);
    }
  }

  requestDeleteItem(id: string): void {
    this.confirming.set({ type: 'item', id });
  }

  async executeDeleteItem(id: string): Promise<void> {
    this.error.set(null);
    this.busy.set(true);
    try {
      await this.api.deleteMenuItem(id);
      this.confirming.set(null);
      this.refresh.emit();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Delete item failed'));
    } finally {
      this.busy.set(false);
    }
  }

  onChangeAvailability(target: EventTarget | null, item: MenuItemDetail, categoryId: string): void {
    const value = (target as HTMLSelectElement)?.value;
    if (!value) return;
    this.error.set(null);
    this.busy.set(true);
    this.api.updateMenuItem(item.id, {
      categoryId,
      stationId: item.stationId,
      name: item.name,
      price: item.price,
      availability: value,
    }).then(() => this.refresh.emit())
      .catch((e: unknown) => this.error.set(this.extractError(e, 'Change availability failed')))
      .finally(() => this.busy.set(false));
  }

  /* ── Item ⇄ Modifier Group attach ── */

  startAttachGroup(itemId: string): void {
    this.attachingItemId.set(itemId);
    this.attachGroupSelect.set('');
  }

  cancelAttachGroup(): void {
    this.attachingItemId.set(null);
    this.attachGroupSelect.set('');
  }

  onAttachGroupSelect(target: EventTarget | null): void {
    this.attachGroupSelect.set((target as HTMLSelectElement)?.value ?? '');
  }

  availableGroupsForItem(item: MenuItemDetail): ModifierGroupDto[] {
    const attached = new Set(item.modifierGroups.map(g => g.id));
    return this.allGroups().filter(g => !attached.has(g.id));
  }

  async attachGroup(itemId: string): Promise<void> {
    const groupId = this.attachGroupSelect();
    if (!groupId) return;
    this.error.set(null);
    this.busy.set(true);
    try {
      await this.api.attachModifierGroup(itemId, groupId);
      this.cancelAttachGroup();
      this.refresh.emit();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Attach group failed'));
    } finally {
      this.busy.set(false);
    }
  }

  requestDetachGroup(itemId: string, groupId: string): void {
    this.confirmAttach.set({ itemId, groupId });
  }

  cancelDetachGroup(): void {
    this.confirmAttach.set(null);
  }

  async executeDetachGroup(itemId: string, groupId: string): Promise<void> {
    this.error.set(null);
    this.busy.set(true);
    try {
      await this.api.detachModifierGroup(itemId, groupId);
      this.confirmAttach.set(null);
      this.refresh.emit();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Detach group failed'));
    } finally {
      this.busy.set(false);
    }
  }

  cancelConfirm(): void {
    this.confirming.set(null);
  }

  private extractError(e: unknown, fallback: string): string {
    const body = (e as Record<string, unknown>)?.['error'] as Record<string, unknown> | undefined;
    return (body?.['title'] as string) || (body?.['detail'] as string) || (e as Error)?.message || fallback;
  }
}
