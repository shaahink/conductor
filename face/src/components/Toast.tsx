import React from "react";
import { Box, Text } from "ink";
import type { ToastItem } from "../state/store.js";
import { colors } from "../theme.js";

const KIND_COLOR: Record<ToastItem["kind"], string> = {
  success: colors.ok,
  error: colors.error,
  warn: colors.warn,
  info: colors.accent,
};

const KIND_GLYPH: Record<ToastItem["kind"], string> = {
  success: "✓",
  error: "✖",
  warn: "⚠",
  info: "ℹ",
};

/** Transient stack, newest at top — every control/inject action gets one (D11: "every action
 * acknowledged with a toast + log line, no silent drops"). */
export function ToastStack({ toasts }: { toasts: ToastItem[] }) {
  if (toasts.length === 0) return null;
  return (
    <Box flexDirection="column" position="absolute" marginTop={1}>
      {toasts.slice(-4).map((t) => (
        <Box key={t.id} borderStyle="round" borderColor={KIND_COLOR[t.kind]} paddingX={1}>
          <Text color={KIND_COLOR[t.kind]}>
            {KIND_GLYPH[t.kind]} {t.text}
          </Text>
        </Box>
      ))}
    </Box>
  );
}
