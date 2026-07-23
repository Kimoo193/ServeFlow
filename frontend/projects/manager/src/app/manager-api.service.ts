import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { API_CONFIG } from 'shared';

/* ── Menu tree ── */

export interface MenuModifier {
  id: string;
  name: string;
  priceDelta: number;
}

export interface MenuModifierGroup {
  id: string;
  name: string;
  minSelect: number;
  maxSelect: number;
  required: boolean;
  modifiers: MenuModifier[];
}

export interface MenuItemDetail {
  id: string;
  name: string;
  price: number;
  availability: 'Available' | 'OutOfStock' | 'Hidden';
  stationId: string;
  modifierGroups: MenuModifierGroup[];
}

export interface MenuCategory {
  id: string;
  name: string;
  sortOrder: number;
  items: MenuItemDetail[];
}

export interface ComboItemLink {
  itemId: string;
  quantity: number;
}

export interface MenuCombo {
  id: string;
  name: string;
  price: number;
  items: ComboItemLink[];
}

export interface MenuTree {
  categories: MenuCategory[];
  combos: MenuCombo[];
}

/* ── Standalone entities ── */

export interface Station {
  id: string;
  branchId: string;
  name: string;
}

export interface ModifierGroupDto {
  id: string;
  name: string;
  minSelect: number;
  maxSelect: number;
  required: boolean;
}

export interface ModifierDto {
  id: string;
  groupId: string;
  name: string;
  priceDelta: number;
}

/* ── Create / update payloads ── */

export interface CreateCategoryRequest {
  name: string;
  sortOrder: number;
}

export interface CreateMenuItemRequest {
  categoryId: string;
  stationId: string;
  name: string;
  price: number;
  availability: string;
}

export interface CreateModifierGroupRequest {
  name: string;
  minSelect: number;
  maxSelect: number;
  required: boolean;
}

export interface CreateModifierRequest {
  groupId: string;
  name: string;
  priceDelta: number;
}

export interface CreateComboRequest {
  name: string;
  price: number;
}

export interface AttachComboItemRequest {
  quantity: number;
}

@Injectable({ providedIn: 'root' })
export class ManagerApiService {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_CONFIG).baseUrl;

  /* ── Menu tree ── */

  getMenuTree(): Promise<MenuTree> {
    return firstValueFrom(
      this.http.get<MenuTree>(`${this.base}/admin/menu`),
    );
  }

  /* ── Stations ── */

  getStations(): Promise<Station[]> {
    return firstValueFrom(
      this.http.get<Station[]>(`${this.base}/stations`),
    );
  }

  /* ── Categories ── */

  createCategory(body: CreateCategoryRequest): Promise<{ id: string; name: string; sortOrder: number }> {
    return firstValueFrom(
      this.http.post<{ id: string; name: string; sortOrder: number }>(`${this.base}/categories`, body),
    );
  }

  updateCategory(id: string, body: CreateCategoryRequest): Promise<{ id: string; name: string; sortOrder: number }> {
    return firstValueFrom(
      this.http.put<{ id: string; name: string; sortOrder: number }>(`${this.base}/categories/${id}`, body),
    );
  }

  deleteCategory(id: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`${this.base}/categories/${id}`),
    );
  }

  /* ── Menu Items ── */

  createMenuItem(body: CreateMenuItemRequest): Promise<{ id: string }> {
    return firstValueFrom(
      this.http.post<{ id: string }>(`${this.base}/menu-items`, body),
    );
  }

  updateMenuItem(id: string, body: CreateMenuItemRequest): Promise<void> {
    return firstValueFrom(
      this.http.put<void>(`${this.base}/menu-items/${id}`, body),
    );
  }

  deleteMenuItem(id: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`${this.base}/menu-items/${id}`),
    );
  }

  /* ── Item ⇄ Modifier Group links ── */

  attachModifierGroup(itemId: string, groupId: string): Promise<void> {
    return firstValueFrom(
      this.http.post<void>(`${this.base}/menu-items/${itemId}/modifier-groups/${groupId}`, {}),
    );
  }

  detachModifierGroup(itemId: string, groupId: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`${this.base}/menu-items/${itemId}/modifier-groups/${groupId}`),
    );
  }

  /* ── Modifier Groups ── */

  getModifierGroups(): Promise<ModifierGroupDto[]> {
    return firstValueFrom(
      this.http.get<ModifierGroupDto[]>(`${this.base}/modifier-groups`),
    );
  }

  createModifierGroup(body: CreateModifierGroupRequest): Promise<ModifierGroupDto> {
    return firstValueFrom(
      this.http.post<ModifierGroupDto>(`${this.base}/modifier-groups`, body),
    );
  }

  updateModifierGroup(id: string, body: CreateModifierGroupRequest): Promise<ModifierGroupDto> {
    return firstValueFrom(
      this.http.put<ModifierGroupDto>(`${this.base}/modifier-groups/${id}`, body),
    );
  }

  deleteModifierGroup(id: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`${this.base}/modifier-groups/${id}`),
    );
  }

  /* ── Modifiers ── */

  getModifiers(): Promise<ModifierDto[]> {
    return firstValueFrom(
      this.http.get<ModifierDto[]>(`${this.base}/modifiers`),
    );
  }

  createModifier(body: CreateModifierRequest): Promise<ModifierDto> {
    return firstValueFrom(
      this.http.post<ModifierDto>(`${this.base}/modifiers`, body),
    );
  }

  updateModifier(id: string, body: CreateModifierRequest): Promise<ModifierDto> {
    return firstValueFrom(
      this.http.put<ModifierDto>(`${this.base}/modifiers/${id}`, body),
    );
  }

  deleteModifier(id: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`${this.base}/modifiers/${id}`),
    );
  }

  /* ── Combos ── */

  createCombo(body: CreateComboRequest): Promise<{ id: string; name: string; price: number }> {
    return firstValueFrom(
      this.http.post<{ id: string; name: string; price: number }>(`${this.base}/combos`, body),
    );
  }

  updateCombo(id: string, body: CreateComboRequest): Promise<{ id: string; name: string; price: number }> {
    return firstValueFrom(
      this.http.put<{ id: string; name: string; price: number }>(`${this.base}/combos/${id}`, body),
    );
  }

  deleteCombo(id: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`${this.base}/combos/${id}`),
    );
  }

  /* ── Combo ⇄ Item links ── */

  attachComboItem(comboId: string, itemId: string, quantity: number): Promise<void> {
    return firstValueFrom(
      this.http.post<void>(`${this.base}/combos/${comboId}/items/${itemId}`, { quantity } as AttachComboItemRequest),
    );
  }

  detachComboItem(comboId: string, itemId: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`${this.base}/combos/${comboId}/items/${itemId}`),
    );
  }
}
