import { describe, expect, it } from "vitest";
import { Store } from "../src/state/store.js";
import { startConnection } from "../src/state/connection.js";
import { StaticDataSource, FailingDataSource } from "./fixtures.js";
import { tick } from "./testUtils.js";

describe("startConnection", () => {
  it("populates the store from a healthy DataSource", async () => {
    const store = new Store("live", "http://127.0.0.1:4317");
    const stop = startConnection(store, new StaticDataSource());
    await tick(20);
    expect(store.getState().planState?.stageId).toBe("F6");
    expect(store.getState().processes).toHaveLength(2);
    stop();
  });

  it("never throws when every endpoint fails — degrades to a disconnected indicator instead of crashing", async () => {
    const store = new Store("live", "http://127.0.0.1:4317");
    const source = new FailingDataSource();
    let stop: () => void = () => {};
    expect(() => {
      stop = startConnection(store, source);
    }).not.toThrow();
    await tick(20);
    // The poll loop swallows rejections (Promise.allSettled) — state stays null, not a thrown error
    // propagating out of the app. This is the connection-layer half of "TUI crash leaves run alive";
    // the process-isolation half (Face is a separate OS process from conductor.exe, HTTP-only) is
    // structural, not something a unit test can exercise.
    expect(store.getState().planState).toBeNull();
    expect(store.getState().connection.lastError).toBeTruthy();
    stop();
  });
});
