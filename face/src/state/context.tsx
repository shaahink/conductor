import React, { createContext, useContext, useSyncExternalStore } from "react";
import type { AppState, Store } from "./store.js";

const StoreCtx = createContext<Store | null>(null);

export function StoreProvider({ store, children }: { store: Store; children: React.ReactNode }) {
  return <StoreCtx.Provider value={store}>{children}</StoreCtx.Provider>;
}

export function useStoreInstance(): Store {
  const store = useContext(StoreCtx);
  if (!store) throw new Error("useStoreInstance called outside StoreProvider");
  return store;
}

/** Subscribes to the whole AppState and re-renders on any change. Fine at this app's scale (a
 * handful of panes, low-frequency updates); a selector-based subscribe isn't worth the complexity
 * for a single-operator terminal UI. */
export function useAppState(): AppState {
  const store = useStoreInstance();
  return useSyncExternalStore(store.subscribe, store.getState, store.getState);
}
