import React, { useMemo, useState } from "react";
import { Box, Text, useInput } from "ink";
import { isMouseSequence } from "../input/mouse.js";
import { colors } from "../theme.js";

export interface TextEditorProps {
  value: string;
  onChange(value: string): void;
  height: number;
  width: number;
  focus?: boolean;
  placeholder?: string;
}

/** A small hand-rolled multi-line editor — no third-party text-input dependency (version drift
 * risk against Ink 5, and this app needs the escape-sequence guard below regardless). Supports
 * arrow navigation, Enter/Backspace/Delete, Home/End, and plain character insertion; no
 * selection/undo/clipboard — a "workable textbox", not a modal editor replacement. */
export function TextEditor({ value, onChange, height, width, focus = true, placeholder }: TextEditorProps) {
  const [cursor, setCursor] = useState(value.length);
  const [scrollTop, setScrollTop] = useState(0);

  const lines = useMemo(() => value.split("\n"), [value]);
  const { row: cursorRow, col: cursorCol } = useMemo(() => offsetToRowCol(value, cursor), [value, cursor]);

  useInput(
    (input, key) => {
      // Defence-in-depth: an SGR mouse escape sequence must never be typed into a text buffer even
      // if it reaches here (see src/input/mouse.ts — mouse tracking runs for the whole app lifetime).
      if (isMouseSequence(input)) return;

      if (key.leftArrow) {
        setCursor((c) => Math.max(0, c - 1));
        return;
      }
      if (key.rightArrow) {
        setCursor((c) => Math.min(value.length, c + 1));
        return;
      }
      if (key.upArrow) {
        setCursor((c) => moveVertical(value, c, -1));
        return;
      }
      if (key.downArrow) {
        setCursor((c) => moveVertical(value, c, 1));
        return;
      }
      if (key.return) {
        const next = value.slice(0, cursor) + "\n" + value.slice(cursor);
        onChange(next);
        setCursor(cursor + 1);
        return;
      }
      if (key.backspace || key.delete) {
        if (cursor === 0) return;
        const next = value.slice(0, cursor - 1) + value.slice(cursor);
        onChange(next);
        setCursor(cursor - 1);
        return;
      }
      if (key.ctrl && (input === "u" || input === "U")) {
        // clear-to-start-of-line, a common terminal-editing reflex
        const startOfLine = value.lastIndexOf("\n", cursor - 1) + 1;
        const next = value.slice(0, startOfLine) + value.slice(cursor);
        onChange(next);
        setCursor(startOfLine);
        return;
      }
      if (input === "") return; // stray DEL byte on some terminals — already handled above
      if (!key.ctrl && !key.meta && input && input.length > 0 && input.charCodeAt(0) >= 0x20) {
        const next = value.slice(0, cursor) + input + value.slice(cursor);
        onChange(next);
        setCursor(cursor + input.length);
      }
    },
    { isActive: focus },
  );

  // Keep the cursor's row inside the visible window.
  const visibleLines = Math.max(1, height);
  let top = scrollTop;
  if (cursorRow < top) top = cursorRow;
  if (cursorRow >= top + visibleLines) top = cursorRow - visibleLines + 1;
  if (top !== scrollTop) setScrollTop(top);

  const shown = lines.slice(top, top + visibleLines);
  const isEmpty = value.length === 0;

  return (
    <Box flexDirection="column" width={width} height={height}>
      {isEmpty && placeholder ? (
        <Text color={colors.dim}>{placeholder}</Text>
      ) : (
        shown.map((line, i) => {
          const actualRow = top + i;
          if (actualRow === cursorRow && focus) {
            const before = line.slice(0, cursorCol);
            const at = line[cursorCol] ?? " ";
            const after = line.slice(cursorCol + 1);
            return (
              <Text key={i} wrap="truncate">
                {before}
                <Text backgroundColor={colors.accent} color="black">
                  {at}
                </Text>
                {after}
              </Text>
            );
          }
          return (
            <Text key={i} wrap="truncate">
              {line.length > 0 ? line : " "}
            </Text>
          );
        })
      )}
    </Box>
  );
}

function offsetToRowCol(value: string, offset: number): { row: number; col: number } {
  const upTo = value.slice(0, offset);
  const row = (upTo.match(/\n/g) ?? []).length;
  const lastNl = upTo.lastIndexOf("\n");
  const col = offset - (lastNl + 1);
  return { row, col };
}

function moveVertical(value: string, offset: number, dir: 1 | -1): number {
  const { row, col } = offsetToRowCol(value, offset);
  const lines = value.split("\n");
  const targetRow = row + dir;
  if (targetRow < 0 || targetRow >= lines.length) return offset;
  const targetLine = lines[targetRow] ?? "";
  const targetCol = Math.min(col, targetLine.length);
  let base = 0;
  for (let i = 0; i < targetRow; i++) base += (lines[i]?.length ?? 0) + 1;
  return base + targetCol;
}
