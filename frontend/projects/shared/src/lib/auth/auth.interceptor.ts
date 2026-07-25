import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { SessionService } from './session.service';

const LOGIN_PATH = '/auth/login';

/** Attaches the terminal's Bearer token to every backend request. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const token = inject(SessionService).token();
  if (!token) return next(req);
  return next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));
};

/**
 * Ends a session the backend has stopped accepting and returns the terminal to
 * its connect screen.
 *
 * Without this a KDS running unattended through a shift keeps a dead token,
 * erroring on every request with no route back — the most likely real outage in
 * the current design.
 */
export const authErrorInterceptor: HttpInterceptorFn = (req, next) => {
  const session = inject(SessionService);
  const router = inject(Router);

  return next(req).pipe(
    catchError((error: unknown) => {
      // A 401 from the login endpoint means "wrong password" — the connect
      // screen reports that itself. Only an in-session 401 means a dead token.
      const inSession = session.isConnected() && !req.url.endsWith(LOGIN_PATH);
      if (inSession && error instanceof HttpErrorResponse && error.status === 401) {
        session.disconnect();
        void router.navigate(['/']);
      }
      return throwError(() => error);
    }),
  );
};
