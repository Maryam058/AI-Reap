// Zoom of the original-document views (OriginalDocumentViews), as a share of the view's own starting
// size: a PDF page fitted to the pane's width, a Word page at its own size (or reflowed to a narrower
// pane). Kept apart from the components so it can be tested on its own.
export const ZOOM_LEVELS = [0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3] as const;

// The next level up or down from any zoom (one between two levels goes to the next one); the same
// zoom at either end.
export function zoomStep(zoom: number, direction: 1 | -1): number {
  const levels = direction > 0 ? ZOOM_LEVELS : [...ZOOM_LEVELS].reverse();
  return levels.find((z) => (direction > 0 ? z > zoom + 1e-6 : z < zoom - 1e-6)) ?? zoom;
}

// Ctrl/Cmd with + - 0 zooms the document (not the app), as in a browser or document reader: the new
// zoom, or null for any other key.
export function zoomForKey(zoom: number, event: Pick<KeyboardEvent, 'key' | 'ctrlKey' | 'metaKey' | 'altKey'>): number | null {
  if (!(event.ctrlKey || event.metaKey) || event.altKey) return null;
  if (event.key === '+' || event.key === '=') return zoomStep(zoom, 1);
  if (event.key === '-' || event.key === '_') return zoomStep(zoom, -1);
  if (event.key === '0') return 1;
  return null;
}
