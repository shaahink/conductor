// SGR mouse mode (xterm protocol) - click + release + wheel, no motion tracking (mode 1003 would
// flood the stream with every pixel of cursor movement; 1000+1006 only reports button transitions
// and wheel ticks, which is all a terminal-friendly UI needs). Supported by essentially every
// modern terminal (Windows Terminal, iTerm2, GNOME Terminal, most xterm-compatible emulators) -
// degrades silently to keyboard-only where it isn't (no escape sequence in the stream, no crash).

export type MouseButton = 0 | 1 | 2 | "release" | "wheelUp" | "wheelDown";

export interface MouseEvent {
  button: MouseButton;
  /** 0-based column. */
  x: number;
  /** 0-based row. */
  y: number;
  shift: boolean;
  meta: boolean;
  ctrl: boolean;
}

// Built from the numeric escape code point rather than a literal control character in the source
// file, so this file stays plain, diffable ASCII (no invisible bytes to trip up an editor/grep).
const ESC = String.fromCharCode(27);

export function enableMouse(stdout: NodeJS.WriteStream): void {
  stdout.write(`${ESC}[?1000h${ESC}[?1006h`);
}

export function disableMouse(stdout: NodeJS.WriteStream): void {
  stdout.write(`${ESC}[?1000l${ESC}[?1006l`);
}

const SGR_PATTERN = "\\[<(\\d+);(\\d+);(\\d+)([mM])";

/** Pure parse of a raw stdin chunk into zero or more mouse events. Exported standalone (no stdin
 * dependency) so it's unit-testable without a real TTY. */
export function parseMouseChunk(chunk: string): MouseEvent[] {
  const events: MouseEvent[] = [];
  const re = new RegExp(SGR_PATTERN, "g");
  let match: RegExpExecArray | null;
  while ((match = re.exec(chunk)) !== null) {
    const cb = Number(match[1]);
    const x = Number(match[2]) - 1;
    const y = Number(match[3]) - 1;
    const isRelease = match[4] === "m";
    const isWheel = (cb & 0x40) !== 0;
    const buttonNum = cb & 0x3;
    const shift = (cb & 0x4) !== 0;
    const meta = (cb & 0x8) !== 0;
    const ctrl = (cb & 0x10) !== 0;
    let button: MouseButton;
    if (isWheel) button = buttonNum === 0 ? "wheelUp" : "wheelDown";
    else if (isRelease) button = "release";
    else button = buttonNum as 0 | 1 | 2;
    events.push({ button, x, y, shift, meta, ctrl });
  }
  return events;
}

/** True if a raw stdin chunk contains an SGR mouse-tracking escape sequence — used by text-input
 * components to refuse to type one into the buffer if it ever reaches them. */
export function isMouseSequence(chunk: string): boolean {
  return chunk.includes("[<");
}

export function attachMouseHandler(stdin: NodeJS.ReadStream, onEvent: (e: MouseEvent) => void): () => void {
  const listener = (data: Buffer | string) => {
    const str = typeof data === "string" ? data : data.toString("utf8");
    if (!str.includes("[<")) return;
    for (const e of parseMouseChunk(str)) onEvent(e);
  };
  stdin.on("data", listener);
  return () => {
    stdin.off("data", listener);
  };
}
