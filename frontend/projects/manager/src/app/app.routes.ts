import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    loadComponent: () =>
      import('./manager-connect/manager-connect').then((m) => m.ManagerConnect),
  },
  {
    path: 'dashboard',
    loadComponent: () =>
      import('./dashboard/dashboard').then((m) => m.Dashboard),
  },
  {
    path: 'menu',
    loadComponent: () =>
      import('./menu/menu').then((m) => m.Menu),
  },
  {
    path: 'floor',
    loadComponent: () =>
      import('./floor/floor').then((m) => m.Floor),
  },
  {
    path: 'history',
    loadComponent: () =>
      import('./history/history').then((m) => m.OrderHistory),
  },
  { path: '**', redirectTo: '' },
];
