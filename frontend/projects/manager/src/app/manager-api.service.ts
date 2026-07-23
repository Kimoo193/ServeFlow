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

export interface Section {
  id: string;
  branchId: string;
  name: string;
}

export interface DiningTable {
  id: string;
  label: string;
  status: string;
  sectionId: string;
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

/* ── Dashboard & History ── */

export interface DashboardSummary {
  date: string;
  ordersToday: number;
  openNow: number;
}

export interface OrderHistoryRow {
  id: string;
  tableLabel: string | null;
  orderType: string;
  status: string;
  dailyNo: number | null;
  customerName: string | null;
  lineCount: number;
  total: number | null;
  createdAt: string;
  completedAt: string | null;
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

  /* ── Sections ── */

  getSections(): Promise<Section[]> {
    return firstValueFrom(
      this.http.get<Section[]>(`${this.base}/sections`),
    );
  }

  createSection(body: { name: string }): Promise<Section> {
    return firstValueFrom(
      this.http.post<Section>(`${this.base}/sections`, body),
    );
  }

  updateSection(id: string, body: { name: string }): Promise<Section> {
    return firstValueFrom(
      this.http.put<Section>(`${this.base}/sections/${id}`, body),
    );
  }

  deleteSection(id: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`${this.base}/sections/${id}`),
    );
  }

  /* ── Tables ── */

  getTables(): Promise<DiningTable[]> {
    return firstValueFrom(
      this.http.get<DiningTable[]>(`${this.base}/tables`),
    );
  }

  createTable(body: { sectionId: string; label: string }): Promise<DiningTable> {
    return firstValueFrom(
      this.http.post<DiningTable>(`${this.base}/tables`, body),
    );
  }

  updateTable(id: string, body: { sectionId: string; label: string }): Promise<DiningTable> {
    return firstValueFrom(
      this.http.put<DiningTable>(`${this.base}/tables/${id}`, body),
    );
  }

  deleteTable(id: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`${this.base}/tables/${id}`),
    );
  }

  /* ── Stations ── */

  getStations(): Promise<Station[]> {
    return firstValueFrom(
      this.http.get<Station[]>(`${this.base}/stations`),
    );
  }

  createStation(body: { branchId: string; name: string }): Promise<Station> {
    return firstValueFrom(
      this.http.post<Station>(`${this.base}/stations`, body),
    );
  }

  updateStation(id: string, body: { branchId: string; name: string }): Promise<Station> {
    return firstValueFrom(
      this.http.put<Station>(`${this.base}/stations/${id}`, body),
    );
  }

  deleteStation(id: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`${this.base}/stations/${id}`),
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

  /* ── Dashboard & History ── */

  getDashboardSummary(): Promise<DashboardSummary> {
    return firstValueFrom(
      this.http.get<DashboardSummary>(`${this.base}/dashboard/summary`),
    );
  }

  getOrderHistory(limit?: number): Promise<OrderHistoryRow[]> {
    const params = limit ? `?limit=${limit}` : '';
    return firstValueFrom(
      this.http.get<OrderHistoryRow[]>(`${this.base}/orders/history${params}`),
    );
  }
}
