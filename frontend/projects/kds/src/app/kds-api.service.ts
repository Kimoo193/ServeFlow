import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { API_CONFIG, MenuItem, TicketDto, TicketLine } from 'shared';

/** All backend reads/writes the KDS station board needs. */
@Injectable({ providedIn: 'root' })
export class KdsApiService {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_CONFIG).baseUrl;

  getTickets(stationId: string): Promise<TicketDto[]> {
    return firstValueFrom(
      this.http.get<TicketDto[]>(`${this.base}/kds/stations/${stationId}/tickets`),
    );
  }

  getMenuItems(): Promise<MenuItem[]> {
    return firstValueFrom(this.http.get<MenuItem[]>(`${this.base}/menu-items`));
  }

  getStationName(stationId: string): Promise<string> {
    return firstValueFrom(
      this.http.get<{ name: string }>(`${this.base}/stations/${stationId}`),
    ).then((s) => s.name);
  }

  startPreparing(lineId: string): Promise<{ status: string }> {
    return firstValueFrom(
      this.http.post<{ status: string }>(
        `${this.base}/kds/lines/${lineId}/start-preparing`,
        {},
      ),
    );
  }

  markReady(lineId: string): Promise<{ status: string }> {
    return firstValueFrom(
      this.http.post<{ status: string }>(`${this.base}/kds/lines/${lineId}/ready`, {}),
    );
  }
}

/** Convenience: the terse item label the KDS shows, name resolved via lookup. */
export function lineLabel(line: TicketLine, names: Map<string, string>): string {
  return names.get(line.itemId) ?? `Item ${line.itemId.slice(0, 4)}`;
}
