/**
 * S2 — a user whose only role is Developer works from the structured artifacts. The API refuses them
 * raw requirement sources and project documents and lists sources without their text or file name.
 * Mirrors ArtifactVisibility.CanReadRawSources on the server; the server is what enforces it.
 */
export const readsRawSources = (hasRole: (role: string) => boolean) =>
  !hasRole('Developer') || ['Administrator', 'BusinessAnalyst', 'QA', 'Reviewer'].some(hasRole);
