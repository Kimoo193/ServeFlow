import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { API_CONFIG } from 'shared';

export interface OpenOrderSummary {
  id: string;
  tableId: string | null;
  tableLabel: string | null;
  orderType: string;
  status: string;
  dailyNo: number | null;
  customerName: string | null;
  lineCount: number;
  unfinishedCount: number;
  rowVersion: number;
}

export interface BillLineModifier {
  name: string;
  priceDelta: number;
}

export interface BillLine {
  id: string;
  itemId: string;
  quantity: number;
  unitPrice: number;
  lineTotal: number;
  modifiers: BillLineModifier[];
}

export interface BillPreview {
  orderId: string;
  canClose: boolean;
  unfinishedCount: number;
  lines: BillLine[];
  subtotal: number;
  serviceAmount: number;
  taxAmount: number;
  discountAmount: number;
  total: number;
  rowVersion: number;
}

export interface ReceiptLine {
  id: string;
  itemId: string;
  quantity: number;
  unitPrice: number;
  lineTotal: number;
  status: string;
  modifiers: BillLineModifier[];
}

export interface Receipt {
  branchId: string;
  orderId: string;
  tableId: string | null;
  dailyNo: number | null;
  waiterId: string;
  orderType: string;
  closedAt: string;
  lines: ReceiptLine[];
  subtotal: number;
  serviceAmount: number;
  taxAmount: number;
  discountAmount: number;
  total: number;
}

export interface CloseResponse {
  orderId: string;
  status: string;
  subtotal: number;
  serviceAmount: number;
  taxAmount: number;
  discountAmount: number;
  total: number;
  rowVersion: number;
}

export interface PrintResponse {
  orderId: string;
  printed: boolean;
}

export interface MenuItem {
  id: string;
  name: string;
  price: number;
}

@Injectable({ providedIn: 'root' })
export class CashierApiService {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_CONFIG).baseUrl;

  getOpenOrders(): Promise<OpenOrderSummary[]> {
    return firstValueFrom(
      this.http.get<OpenOrderSummary[]>(`${this.base}/orders/open`),
    );
  }

  getBillPreview(orderId: string): Promise<BillPreview> {
    return firstValueFrom(
      this.http.get<BillPreview>(`${this.base}/orders/${orderId}/bill-preview`),
    );
  }

  closeOrder(orderId: string, rowVersion: number): Promise<CloseResponse> {
    return firstValueFrom(
      this.http.post<CloseResponse>(`${this.base}/orders/${orderId}/close`, { rowVersion }),
    );
  }

  getReceipt(orderId: string): Promise<Receipt> {
    return firstValueFrom(
      this.http.get<Receipt>(`${this.base}/orders/${orderId}/receipt`),
    );
  }

  printReceipt(orderId: string): Promise<PrintResponse> {
    return firstValueFrom(
      this.http.post<PrintResponse>(`${this.base}/orders/${orderId}/receipt/print`, {}),
    );
  }

  getMenuItems(): Promise<MenuItem[]> {
    return firstValueFrom(this.http.get<MenuItem[]>(`${this.base}/menu-items`));
  }
}
