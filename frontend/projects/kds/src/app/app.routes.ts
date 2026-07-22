import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    loadComponent: () =>
      import('./station-connect/station-connect').then((m) => m.StationConnect),
  },
  {
    path: 'board/:stationId',
    loadComponent: () =>
      import('./station-board/station-board').then((m) => m.StationBoard),
  },
  { path: '**', redirectTo: '' },
];
