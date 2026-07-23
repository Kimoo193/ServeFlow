import { Component, inject, signal, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { SessionService } from 'shared';
import { ManagerApiService, MenuTree, Station } from '../manager-api.service';
import { CategoryPanel } from './category-panel/category-panel';
import { ModifierLibrary } from './modifier-library/modifier-library';
import { ComboPanel } from './combo-panel/combo-panel';

@Component({
  selector: 'app-menu',
  imports: [CategoryPanel, ModifierLibrary, ComboPanel],
  templateUrl: './menu.html',
  styleUrl: './menu.css',
})
export class Menu implements OnInit {
  private readonly api = inject(ManagerApiService);
  private readonly session = inject(SessionService);
  private readonly router = inject(Router);

  readonly activeTab = signal<'categories' | 'modifiers' | 'combos'>('categories');
  readonly error = signal<string | null>(null);
  readonly menuTree = signal<MenuTree | null>(null);
  readonly stations = signal<Station[]>([]);

  ngOnInit(): void {
    if (!this.session.isConnected()) {
      this.router.navigate(['/']);
      return;
    }
    this.loadAll();
  }

  private async loadAll(): Promise<void> {
    try {
      const [tree, stations] = await Promise.all([
        this.api.getMenuTree(),
        this.api.getStations(),
      ]);
      this.menuTree.set(tree);
      this.stations.set(stations);
      this.error.set(null);
    } catch (e: unknown) {
      const body = (e as Record<string, unknown>)?.['error'] as Record<string, unknown> | undefined;
      this.error.set((body?.['title'] as string) || (e as Error)?.message || 'Failed to load menu data');
    }
  }

  refreshTree(): void {
    this.loadAll();
  }

  closeError(): void {
    this.error.set(null);
  }
}
