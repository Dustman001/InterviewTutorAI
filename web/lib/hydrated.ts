import { useSyncExternalStore } from "react";

const subscribe = () => () => {};

/**
 * False in the server-rendered HTML and until React takes over the page; true after. Submit buttons stay disabled
 * until then: a click before that submits the form natively, which only reloads the page and loses the click.
 */
export function useHydrated(): boolean {
  return useSyncExternalStore(
    subscribe,
    () => true,
    () => false,
  );
}
