import React, { useMemo, useState } from "react";
import { Box, Text, useInput } from "ink";
import { Modal } from "./Modal.js";
import { colors, stateColor } from "../theme.js";
import { isMouseSequence } from "../input/mouse.js";
import type { SessionRowDto, TranscriptLineDto } from "../api/types.js";

export interface SessionHistoryProps {
  visible: boolean;
  sessions: SessionRowDto[];
  transcript: TranscriptLineDto[];
  onClose(): void;
  width: number;
  height: number;
}

/** F6.4 session-history browser (D11): "what did session N actually do" — the sessions table
 * (run.db, already queryable via `conductor report --query`) plus that session's slice of the
 * buffered transcript stream, filtered by sessionId. */
export function SessionHistory({ visible, sessions, transcript, onClose, width, height }: SessionHistoryProps) {
  const [selected, setSelected] = useState(0);

  useInput(
    (input, key) => {
      if (isMouseSequence(input)) return;
      if (key.escape) {
        onClose();
        return;
      }
      if (key.upArrow) setSelected((s) => Math.max(0, s - 1));
      if (key.downArrow) setSelected((s) => Math.min(Math.max(0, sessions.length - 1), s + 1));
    },
    { isActive: visible },
  );

  if (!visible) return null;

  const session = sessions[selected];
  const lines = useMemo(
    () => (session ? transcript.filter((l) => l.sessionId === String(session.number)).slice(-12) : []),
    [session, transcript],
  );

  const listWidth = Math.min(34, Math.floor(width * 0.35));

  return (
    <Modal title="SESSION HISTORY" width={width} height={height} footer="Up/Down: select · Esc: close">
      <Box>
        <Box flexDirection="column" width={listWidth} marginRight={2}>
          {sessions.length === 0 && <Text color={colors.dim}>no sessions recorded yet</Text>}
          {sessions.map((s, i) => (
            <Text key={s.number} backgroundColor={i === selected ? colors.accentDim : undefined}>
              {i === selected ? "▶ " : "  "}
              s{s.number} <Text color={stateColor(s.outcome ?? "")}>{s.outcome ?? "—"}</Text> {s.stageId}
            </Text>
          ))}
        </Box>
        <Box flexDirection="column" flexGrow={1}>
          {!session ? (
            <Text color={colors.dim}>select a session</Text>
          ) : (
            <Box flexDirection="column">
              <Text bold>
                Session #{session.number} — {session.kind} · {session.stageId}
              </Text>
              <Text color={colors.dim}>
                started {session.startedUtc} {session.endedUtc ? `→ ended ${session.endedUtc}` : "(running)"}
              </Text>
              <Text>attempt {session.attempt} · resumes {session.resumeCount} · commits {session.commitCount}</Text>
              <Text color={colors.dim}>gates: {session.gateSummary ?? "—"}</Text>
              <Text>{session.resultSummary ?? "no result summary recorded"}</Text>
              <Box marginTop={1} flexDirection="column">
                <Text color={colors.dim}>transcript tail (buffered this connection):</Text>
                {lines.length === 0 && <Text color={colors.dim}>  (none buffered — connect earlier in the session to capture it)</Text>}
                {lines.map((l) => (
                  <Text key={l.seq} dimColor={l.kind === "thinking"} italic={l.kind === "thinking"}>
                    {"  "}
                    {l.text.slice(0, Math.max(10, width - 10))}
                  </Text>
                ))}
              </Box>
            </Box>
          )}
        </Box>
      </Box>
    </Modal>
  );
}
