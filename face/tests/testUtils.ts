import { EventEmitter } from "node:events";
import { render as inkRender } from "ink";
import type { ReactElement } from "react";

// ink-testing-library hardcodes columns=100 with no way to override it, which is unusable for the
// F6.5 golden-layout requirement (80x24 / 120x30 / 200x50). This mirrors its fake stdout/stdin/
// stderr shape (see node_modules/ink-testing-library) but with configurable dimensions, and calls
// Ink's own `render` directly the same way ink-testing-library does internally.

class FakeStdout extends EventEmitter {
  frames: string[] = [];
  private _lastFrame?: string;
  constructor(
    public columns: number,
    public rows: number,
  ) {
    super();
  }
  write = (frame: string) => {
    this.frames.push(frame);
    this._lastFrame = frame;
  };
  lastFrame = () => this._lastFrame;
}

class FakeStderr extends EventEmitter {
  frames: string[] = [];
  write = (frame: string) => {
    this.frames.push(frame);
  };
}

class FakeStdin extends EventEmitter {
  isTTY = true;
  write = (data: string) => {
    this.emit("data", data);
  };
  setEncoding() {
    /* no-op */
  }
  setRawMode() {
    /* no-op */
  }
  resume() {
    /* no-op */
  }
  pause() {
    /* no-op */
  }
  ref() {
    /* no-op */
  }
  unref() {
    /* no-op */
  }
}

export function renderAt(tree: ReactElement, columns: number, rows: number) {
  const stdout = new FakeStdout(columns, rows);
  const stderr = new FakeStderr();
  const stdin = new FakeStdin();
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const instance = inkRender(tree, {
    stdout: stdout as any,
    stderr: stderr as any,
    stdin: stdin as any,
    debug: true,
    exitOnCtrlC: false,
    patchConsole: false,
  });
  return { instance, stdout, stderr, stdin };
}

/** Flush pending microtasks/macrotasks (promise resolutions + the effects they trigger) so an
 * async data load has landed before the next snapshot/assertion. */
export function tick(ms = 0): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
