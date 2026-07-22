import { InjectionToken } from '@angular/core';

/** Backend origin, e.g. http://localhost:5299. Provided per app. */
export interface ApiConfig {
  baseUrl: string;
}

export const API_CONFIG = new InjectionToken<ApiConfig>('sf.api-config');
