import React from "react";
import { Box, Text } from "ink";
import { colors } from "../theme.js";

export interface ModalProps {
  title: string;
  width: number;
  height: number;
  children: React.ReactNode;
  footer?: string;
}

/** Shared overlay chrome for every modal (palette/inject/prompt editor/session history/report/
 * help) — consistent framing so the app reads as one system rather than five bolted-on popups. */
export function Modal({ title, width, height, children, footer }: ModalProps) {
  return (
    <Box position="absolute" flexDirection="column" width={width} height={height} borderStyle="double" borderColor={colors.accent} padding={1}>
      <Text bold color={colors.accent}>
        {title}
      </Text>
      <Box flexDirection="column" flexGrow={1} marginTop={1}>
        {children}
      </Box>
      {footer && (
        <Box marginTop={1}>
          <Text color={colors.dim}>{footer}</Text>
        </Box>
      )}
    </Box>
  );
}
