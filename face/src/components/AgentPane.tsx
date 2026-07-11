import React, { useMemo } from "react";
import { Box, Text } from "ink";
import type { TranscriptLineDto } from "../api/types.js";
import { colors, kindGlyph } from "../theme.js";

export interface AgentPaneProps {
  transcript: TranscriptLineDto[];
  width: number;
  height: number;
  focused: boolean;
  autoScroll: boolean;
  offset: number;
  search: string | null;
  foldTools: boolean;
  connected: boolean;
}

/** F6.3: live transcript WITH thinking stream, scrollback, tool-call folding, search highlight.
 * Rendering is pure/props-driven — all interaction (scroll offset, search term, fold toggle) is
 * App-owned state, so this component stays snapshot-testable at any terminal size. */
export function AgentPane({ transcript, width, height, focused, autoScroll, offset, search, foldTools, connected }: AgentPaneProps) {
  const displayLines = useMemo(() => buildDisplayLines(transcript, foldTools), [transcript, foldTools]);

  const visibleRows = Math.max(1, height - (search != null ? 1 : 0));
  const maxOffset = Math.max(0, displayLines.length - visibleRows);
  const clampedOffset = Math.min(offset, maxOffset);
  const bottom = autoScroll ? displayLines.length : displayLines.length - clampedOffset;
  const top = Math.max(0, bottom - visibleRows);
  const shown = displayLines.slice(top, bottom);

  return (
    <Box flexDirection="column" width={width} height={height}>
      {shown.length === 0 && (
        <Text color={colors.dim}>{connected ? "waiting for agent output…" : "not connected — no transcript stream"}</Text>
      )}
      {shown.map((line) => (
        <TranscriptRow key={line.seq} line={line} width={width} search={search} />
      ))}
      {search != null && (
        <Text color={colors.accent}>
          /{search} {matchCount(displayLines, search)} match(es) — n/N to jump, Esc to clear
        </Text>
      )}
      {!autoScroll && (
        <Box position="absolute" marginLeft={Math.max(0, width - 22)}>
          <Text color={colors.warn}>scrolled — press l for live tail</Text>
        </Box>
      )}
    </Box>
  );
}

export interface DisplayLine extends TranscriptLineDto {
  folded?: boolean;
}

export function buildDisplayLines(transcript: TranscriptLineDto[], foldTools: boolean): DisplayLine[] {
  if (!foldTools) return transcript;
  // Collapse a run of consecutive tool-call lines into a single "N tool calls" summary row.
  const out: DisplayLine[] = [];
  let i = 0;
  while (i < transcript.length) {
    const line = transcript[i];
    if (!line) break;
    if (line.kind === "tool") {
      let j = i;
      while (j < transcript.length && transcript[j]?.kind === "tool") j++;
      const count = j - i;
      if (count > 1) {
        out.push({ ...line, text: `${count} tool calls (last: ${transcript[j - 1]?.text ?? ""})`, folded: true });
      } else {
        out.push(line);
      }
      i = j;
    } else {
      out.push(line);
      i++;
    }
  }
  return out;
}

function matchCount(lines: DisplayLine[], search: string): number {
  return findMatchIndices(lines, search).length;
}

/** Indices (into `lines`) of every line whose text contains `search`, case-insensitively. Shared
 * with App.tsx's n/N "jump to match" so the two never disagree on what counts as a match. */
export function findMatchIndices(lines: DisplayLine[], search: string): number[] {
  const needle = search.toLowerCase();
  if (needle.length === 0) return [];
  const out: number[] = [];
  lines.forEach((l, i) => {
    if (l.text.toLowerCase().includes(needle)) out.push(i);
  });
  return out;
}

function TranscriptRow({ line, width, search }: { line: DisplayLine; width: number; search: string | null }) {
  const isThinking = line.kind === "thinking";
  const color = isThinking ? colors.dim : line.kind === "stderr" ? colors.error : line.kind === "result" ? colors.ok : colors.text;
  const time = new Date(line.ts);
  const hh = time.getHours().toString().padStart(2, "0");
  const mm = time.getMinutes().toString().padStart(2, "0");
  const ss = time.getSeconds().toString().padStart(2, "0");
  const prefix = `${hh}:${mm}:${ss} ${kindGlyph(line.kind)} `;
  const budget = Math.max(4, width - prefix.length);
  const text = line.text.length > budget ? line.text.slice(0, budget - 1) + "…" : line.text;

  const matched = search && search.length > 0 && line.text.toLowerCase().includes(search.toLowerCase());

  return (
    <Text color={color} italic={isThinking} backgroundColor={matched ? "yellow" : undefined}>
      <Text color={colors.dim}>{prefix}</Text>
      {matched ? <Text color="black">{text}</Text> : text}
    </Text>
  );
}
