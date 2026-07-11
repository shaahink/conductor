// Minimal SSE client over the global `fetch` (Node 18+ ships one via undici) — no browser
// EventSource exists in Node, and pulling in a dependency for ~30 lines of "split on \n\n, strip
// 'data: '" isn't worth it. Reconnects with backoff; the caller gets a live `connected` flag.

export interface SseHandle {
  stop(): void;
}

export interface SseOptions {
  onMessage(data: string): void;
  onConnectedChange?(connected: boolean): void;
  /** Query param appended so a reconnect resumes from the last-seen seq, not the top of the file. */
  since?: () => number | undefined;
  signal?: AbortSignal;
}

const RECONNECT_DELAYS_MS = [500, 1000, 2000, 4000, 8000, 8000, 8000];

export function connectSse(url: string, opts: SseOptions): SseHandle {
  let stopped = false;
  let attempt = 0;
  let currentAbort: AbortController | null = null;

  async function loop() {
    while (!stopped) {
      currentAbort = new AbortController();
      const onOuterAbort = () => currentAbort?.abort();
      opts.signal?.addEventListener("abort", onOuterAbort, { once: true });
      try {
        const since = opts.since?.();
        const target = since ? `${url}${url.includes("?") ? "&" : "?"}since=${since}` : url;
        const res = await fetch(target, { signal: currentAbort.signal });
        if (!res.ok || !res.body) throw new Error(`SSE ${url} -> ${res.status}`);
        opts.onConnectedChange?.(true);
        attempt = 0;
        await pump(res.body, opts.onMessage, currentAbort.signal);
      } catch {
        // fall through to reconnect below — every failure (network, abort, parse) is treated the
        // same: the stream is not fatal, just retried.
      } finally {
        opts.signal?.removeEventListener("abort", onOuterAbort);
      }
      opts.onConnectedChange?.(false);
      if (stopped || opts.signal?.aborted) return;
      const delay = RECONNECT_DELAYS_MS[Math.min(attempt, RECONNECT_DELAYS_MS.length - 1)] ?? 8000;
      attempt++;
      await sleep(delay);
    }
  }

  loop();

  return {
    stop() {
      stopped = true;
      currentAbort?.abort();
    },
  };
}

async function pump(
  body: ReadableStream<Uint8Array>,
  onMessage: (data: string) => void,
  signal: AbortSignal,
): Promise<void> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  try {
    while (true) {
      if (signal.aborted) return;
      const { done, value } = await reader.read();
      if (done) return;
      buffer += decoder.decode(value, { stream: true });
      let idx: number;
      // SSE frames are separated by a blank line; a frame may carry multiple "data:" lines.
      while ((idx = buffer.indexOf("\n\n")) !== -1) {
        const frame = buffer.slice(0, idx);
        buffer = buffer.slice(idx + 2);
        const dataLines = frame
          .split("\n")
          .filter((l) => l.startsWith("data:"))
          .map((l) => l.slice(5).trimStart());
        if (dataLines.length > 0) onMessage(dataLines.join("\n"));
      }
    }
  } finally {
    try {
      reader.releaseLock();
    } catch {
      /* best effort */
    }
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
