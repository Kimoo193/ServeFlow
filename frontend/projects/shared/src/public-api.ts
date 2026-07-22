/*
 * Public API of @serveflow/shared — the design system, domain models, auth,
 * and realtime plumbing shared by the KDS, Cashier, and Manager apps.
 */
export * from './lib/config/api-config';
export * from './lib/domain/order-line-status';
export * from './lib/domain/models';
export * from './lib/auth/session.service';
export * from './lib/auth/auth.interceptor';
export * from './lib/realtime/pos-hub.service';
