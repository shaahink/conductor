import { describe, expect, it } from "vitest";
import { parseMouseChunk, isMouseSequence } from "../src/input/mouse.js";

const ESC = String.fromCharCode(27);

describe("parseMouseChunk", () => {
  it("parses a left-click press", () => {
    const events = parseMouseChunk(`${ESC}[<0;12;5M`);
    expect(events).toEqual([{ button: 0, x: 11, y: 4, shift: false, meta: false, ctrl: false }]);
  });

  it("parses a release", () => {
    const events = parseMouseChunk(`${ESC}[<0;12;5m`);
    expect(events[0]?.button).toBe("release");
  });

  it("parses wheel up and wheel down", () => {
    const up = parseMouseChunk(`${ESC}[<64;1;1M`);
    const down = parseMouseChunk(`${ESC}[<65;1;1M`);
    expect(up[0]?.button).toBe("wheelUp");
    expect(down[0]?.button).toBe("wheelDown");
  });

  it("decodes shift/meta/ctrl modifier bits", () => {
    // button 0 + shift(4) + meta(8) + ctrl(16) = 28
    const events = parseMouseChunk(`${ESC}[<28;1;1M`);
    expect(events[0]).toMatchObject({ button: 0, shift: true, meta: true, ctrl: true });
  });

  it("parses multiple sequences in one chunk", () => {
    const events = parseMouseChunk(`${ESC}[<0;1;1M${ESC}[<0;1;1m`);
    expect(events).toHaveLength(2);
    expect(events[0]?.button).toBe(0);
    expect(events[1]?.button).toBe("release");
  });

  it("ignores plain text with no escape sequence", () => {
    expect(parseMouseChunk("hello world")).toEqual([]);
  });

  it("coordinates beyond column 223 decode correctly (SGR extended range)", () => {
    const events = parseMouseChunk(`${ESC}[<0;300;60M`);
    expect(events[0]).toMatchObject({ x: 299, y: 59 });
  });
});

describe("isMouseSequence", () => {
  it("detects an SGR mouse sequence", () => {
    expect(isMouseSequence(`${ESC}[<0;1;1M`)).toBe(true);
  });
  it("returns false for ordinary keyboard input", () => {
    expect(isMouseSequence("hello")).toBe(false);
    expect(isMouseSequence(`${ESC}[A`)).toBe(false); // plain arrow key, not a mouse sequence
  });
});
