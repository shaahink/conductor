import type { DataSource } from "../api/dataSource.js";
import type { Store } from "./store.js";

const POLL_MS = 1000;

/** Wires a DataSource (live HTTP or offline demo) into the store: polls the snapshot endpoints,
 * subscribes to the two SSE streams, and keeps polling even if one request fails (transient engine
 * hiccups must never freeze the UI — the connection indicator is the signal, not a crash). */
export function startConnection(store: Store, source: DataSource): () => void {
  let stopped = false;
  const abort = new AbortController();

  async function pollOnce() {
    const results = await Promise.allSettled([
      source.getState(abort.signal),
      source.getTasks(abort.signal),
      source.getProcesses(abort.signal),
      source.getSessions(abort.signal),
    ]);
    const [stateRes, tasksRes, procRes, sessRes] = results;
    if (stateRes.status === "fulfilled") {
      store.setPlanState(stateRes.value);
      store.setConnection({ lastError: null });
    } else {
      store.setConnection({ lastError: String(stateRes.reason?.message ?? stateRes.reason) });
    }
    if (tasksRes.status === "fulfilled") store.setTasks(tasksRes.value.tasks);
    if (procRes.status === "fulfilled") store.setProcesses(procRes.value.processes);
    if (sessRes.status === "fulfilled") store.setSessions(sessRes.value.sessions);
  }

  async function pollLoop() {
    while (!stopped) {
      await pollOnce();
      await sleep(POLL_MS);
    }
  }

  pollLoop();

  const unsubEvents = source.subscribeEvents(
    (evt) => store.pushEvent(evt),
    (connected) => store.setConnection({ eventsConnected: connected }),
  );
  const unsubTranscript = source.subscribeTranscript(
    (line) => store.pushTranscript(line),
    (connected) => store.setConnection({ transcriptConnected: connected }),
  );

  return () => {
    stopped = true;
    abort.abort();
    unsubEvents();
    unsubTranscript();
    source.dispose();
  };
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
