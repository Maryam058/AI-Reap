import { createContext, useContext, useState } from 'react';

/**
 * Expand/collapse state for generated collections, derived during render (no effects).
 * Every open/close request — a click, "Expand all", a reveal after generation — carries a
 * monotonically increasing stamp; the most recent request wins, otherwise the default applies.
 */
export interface DisclosureRequest {
  stamp: number;
  open: boolean;
}

let lastStamp = 0;
/** Call from event handlers only. */
export const nextStamp = () => ++lastStamp;

/** "Expand all" / "Collapse all" issued by the enclosing collection section. */
export const BulkDisclosureContext = createContext<DisclosureRequest | null>(null);

export function useDisclosure(defaultOpen: boolean, reveal?: DisclosureRequest | null) {
  const bulk = useContext(BulkDisclosureContext);
  const [local, setLocal] = useState<DisclosureRequest | null>(null);
  const latest = [local, bulk, reveal ?? null].reduce<DisclosureRequest | null>(
    (best, r) => (r && (!best || r.stamp > best.stamp) ? r : best),
    null,
  );
  const open = latest ? latest.open : defaultOpen;
  const toggle = () => setLocal({ stamp: nextStamp(), open: !open });
  return { open, toggle };
}
