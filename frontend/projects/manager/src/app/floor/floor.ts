import { Component, inject, OnInit, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { SessionService } from 'shared';
import { ManagerApiService, Section, DiningTable, Station } from '../manager-api.service';

@Component({
  selector: 'app-floor',
  imports: [ReactiveFormsModule],
  templateUrl: './floor.html',
  styleUrl: './floor.css',
})
export class Floor implements OnInit {
  private readonly api = inject(ManagerApiService);
  private readonly session = inject(SessionService);
  private readonly router = inject(Router);

  readonly sections = signal<Section[]>([]);
  readonly tables = signal<DiningTable[]>([]);
  readonly stations = signal<Station[]>([]);
  readonly error = signal<string | null>(null);

  /* ── Section form ── */
  readonly creatingSection = signal(false);
  readonly editingSectionId = signal<string | null>(null);
  readonly sectionForm = new FormGroup({
    name: new FormControl('', Validators.required),
  });

  /* ── Table form ── */
  readonly creatingTable = signal(false);
  readonly editingTableId = signal<string | null>(null);
  readonly tableForm = new FormGroup({
    sectionId: new FormControl('', Validators.required),
    label: new FormControl('', Validators.required),
  });

  /* ── Station form ── */
  readonly creatingStation = signal(false);
  readonly editingStationId = signal<string | null>(null);
  readonly stationForm = new FormGroup({
    name: new FormControl('', Validators.required),
  });

  /* ── Confirm delete ── */
  readonly confirming = signal<{ type: 'section' | 'table' | 'station'; id: string } | null>(null);
  readonly busy = signal(false);

  ngOnInit(): void {
    if (!this.session.isConnected()) {
      this.router.navigate(['/']);
      return;
    }
    this.loadAll();
  }

  private async loadAll(): Promise<void> {
    try {
      const [sections, tables, stations] = await Promise.all([
        this.api.getSections(),
        this.api.getTables(),
        this.api.getStations(),
      ]);
      this.sections.set(sections);
      this.tables.set(tables);
      this.stations.set(stations);
      this.error.set(null);
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Failed to load floor data'));
    }
  }

  tablesForSection(sectionId: string): DiningTable[] {
    return this.tables().filter(t => t.sectionId === sectionId);
  }

  /* ── Section CRUD ── */

  startCreateSection(): void {
    this.creatingSection.set(true);
    this.editingSectionId.set(null);
    this.sectionForm.reset({ name: '' });
  }

  startEditSection(s: Section): void {
    this.creatingSection.set(false);
    this.editingSectionId.set(s.id);
    this.sectionForm.setValue({ name: s.name });
  }

  cancelSectionEdit(): void {
    this.creatingSection.set(false);
    this.editingSectionId.set(null);
  }

  async saveSection(): Promise<void> {
    if (this.sectionForm.invalid) return;
    this.error.set(null);
    this.busy.set(true);
    try {
      const val = this.sectionForm.value as { name: string };
      if (this.editingSectionId()) {
        await this.api.updateSection(this.editingSectionId()!, val);
      } else {
        await this.api.createSection(val);
      }
      this.cancelSectionEdit();
      await this.loadAll();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Save section failed'));
    } finally {
      this.busy.set(false);
    }
  }

  requestDeleteSection(id: string): void {
    this.confirming.set({ type: 'section', id });
  }

  async executeDeleteSection(id: string): Promise<void> {
    this.error.set(null);
    this.busy.set(true);
    try {
      await this.api.deleteSection(id);
      this.confirming.set(null);
      await this.loadAll();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Delete section failed'));
    } finally {
      this.busy.set(false);
    }
  }

  /* ── Table CRUD ── */

  startCreateTable(): void {
    this.creatingTable.set(true);
    this.editingTableId.set(null);
    this.tableForm.setValue({
      sectionId: this.sections()[0]?.id ?? '',
      label: '',
    });
  }

  startEditTable(t: DiningTable): void {
    this.creatingTable.set(false);
    this.editingTableId.set(t.id);
    this.tableForm.setValue({ sectionId: t.sectionId, label: t.label });
  }

  cancelTableEdit(): void {
    this.creatingTable.set(false);
    this.editingTableId.set(null);
  }

  async saveTable(): Promise<void> {
    if (this.tableForm.invalid) return;
    this.error.set(null);
    this.busy.set(true);
    try {
      const val = this.tableForm.value as { sectionId: string; label: string };
      if (this.editingTableId()) {
        await this.api.updateTable(this.editingTableId()!, val);
      } else {
        await this.api.createTable(val);
      }
      this.cancelTableEdit();
      await this.loadAll();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Save table failed'));
    } finally {
      this.busy.set(false);
    }
  }

  requestDeleteTable(id: string): void {
    this.confirming.set({ type: 'table', id });
  }

  async executeDeleteTable(id: string): Promise<void> {
    this.error.set(null);
    this.busy.set(true);
    try {
      await this.api.deleteTable(id);
      this.confirming.set(null);
      await this.loadAll();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Delete table failed'));
    } finally {
      this.busy.set(false);
    }
  }

  /* ── Station CRUD ── */

  startCreateStation(): void {
    this.creatingStation.set(true);
    this.editingStationId.set(null);
    this.stationForm.reset({ name: '' });
  }

  startEditStation(s: Station): void {
    this.creatingStation.set(false);
    this.editingStationId.set(s.id);
    this.stationForm.setValue({ name: s.name });
  }

  cancelStationEdit(): void {
    this.creatingStation.set(false);
    this.editingStationId.set(null);
  }

  async saveStation(): Promise<void> {
    if (this.stationForm.invalid) return;
    this.error.set(null);
    this.busy.set(true);
    try {
      const ctx = this.session.context();
      const body = { branchId: ctx?.branchId ?? '', name: this.stationForm.value.name! };
      if (this.editingStationId()) {
        await this.api.updateStation(this.editingStationId()!, body);
      } else {
        await this.api.createStation(body);
      }
      this.cancelStationEdit();
      await this.loadAll();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Save station failed'));
    } finally {
      this.busy.set(false);
    }
  }

  requestDeleteStation(id: string): void {
    this.confirming.set({ type: 'station', id });
  }

  async executeDeleteStation(id: string): Promise<void> {
    this.error.set(null);
    this.busy.set(true);
    try {
      await this.api.deleteStation(id);
      this.confirming.set(null);
      await this.loadAll();
    } catch (e: unknown) {
      this.error.set(this.extractError(e, 'Delete station failed'));
    } finally {
      this.busy.set(false);
    }
  }

  /* ── Shared ── */

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
