/**
 * KDS runtime config. In production this backend origin is replaced at deploy
 * time (reverse-proxy or build-time file replacement); dev points at the local
 * API from CLAUDE.md's run command (dotnet run --urls http://localhost:5299).
 */
export const environment = {
  apiBaseUrl: 'http://localhost:5299',
};
