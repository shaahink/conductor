import React from "react";
import { Box, Text } from "ink";
import type { ConnectionState } from "../state/store.js";
import type { StateDto } from "../api/types.js";
import { colors, stateColor } from "../theme.js";

function fmtUsd(n: number): string {
  return `$${n.toFixed(2)}`;
}

function fmtTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}k`;
  return String(n);
}

function fmtWall(sec: number): string {
  const m = Math.floor(sec / 60);
  const s = Math.floor(sec % 60);
  return `${m}m${s.toString().padStart(2, "0")}s`;
}

/** Always-visible one-line status bar (D11): run + session cost, tokens, wall time, connection
 * state. "gate cache hits" (D11's literal wording) has no data source yet — that's F7 (gate
 * caching by SHA) — so this shows live gate battery state instead of fabricating a hit counter.
 *
 * Tiered by `columns` rather than showing everything everywhere: cramming the full field set into
 * one line at 80 columns overflowed the row and Ink's Text word-wrap silently grew the ticker to 2
 * lines, which (since Layout budgets exactly 1 row for it) pushed every pane below it down by one —
 * the same class of height-budget bug the golden test caught in PaneFrame. Hiding fields at narrow
 * widths keeps this row's natural width under `columns` at every tier instead of truncating mid-token. */
export function Ticker({ state, connection, columns }: { state: StateDto | null; connection: ConnectionState; columns: number }) {
  if (!state) {
    return (
      <Box paddingX={1}>
        <Text color={colors.dim} wrap="truncate-end">
          connecting to {connection.url}…
        </Text>
      </Box>
    );
  }

  const medium = columns >= 100;
  const wide = columns >= 160;

  const connGlyph = connection.mode === "demo" ? "◆" : connection.eventsConnected ? "●" : "○";
  const connColor = connection.mode === "demo" ? colors.accent : connection.eventsConnected ? colors.ok : colors.error;
  const gatesText = state.gates.length > 0 ? state.gates.map((g) => `${g.name}:${g.state}`).join(" ") : state.gateSummary || "no gates running";

  return (
    <Box paddingX={1} width={columns - 2}>
      <Box flexShrink={0} gap={medium ? 2 : 1}>
        <Text color={connColor}>
          {connGlyph} {connection.mode === "demo" ? "demo" : connection.eventsConnected ? "live" : "reconnecting"}
        </Text>
        <Text color={stateColor(state.status)}>{state.status}</Text>
        <Text wrap="truncate-end">
          {state.stageId} · s{state.sessionNumber}
          {medium ? ` ${state.sessionKind} (${state.attempt}/${state.maxAttempts})` : ""}
        </Text>
        {wide ? (
          <Text color={colors.dim} wrap="truncate-end">
            {gatesText}
          </Text>
        ) : null}
      </Box>
      <Box flexGrow={1} />
      <Box flexShrink={0} gap={medium ? 2 : 1}>
        {medium && (
          <>
            <Text color={colors.dim}>session</Text>
            <Text>{fmtUsd(state.sessionCostUsd)}</Text>
            <Text>{fmtWall(state.sessionElapsedSec)}</Text>
            <Text color={colors.dim}>· run</Text>
          </>
        )}
        <Text color={colors.accent}>{fmtUsd(state.totalCostUsd)}</Text>
        {wide && (
          <Text>
            {fmtTokens(state.tokensInput)}/{fmtTokens(state.tokensOutput)}/{fmtTokens(state.tokensReasoning)}
          </Text>
        )}
      </Box>
    </Box>
  );
}
