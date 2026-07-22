import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    loadComponent: () =>
      import('./cashier-connect/cashier-connect').then((m) => m.CashierConnect),
  },
  {
    path: 'orders',
    loadComponent: () =>
      import('./open-orders/open-orders').then((m) => m.OpenOrders),
  },
  {
    path: 'orders/:id',
    loadComponent: () =>
      import('./order-review/order-review').then((m) => m.OrderReview),
  },
  { path: '**', redirectTo: '' },
];
