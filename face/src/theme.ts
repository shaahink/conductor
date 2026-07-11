// Terminal-safe color tokens (ANSI 16/256-color names Ink/chalk understand — no truecolor
// dependency, so this still looks right over SSH / a default Windows Terminal profile).

export const colors = {
  accent: "cyan",
  accentDim: "blue",
  ok: "green",
  warn: "yellow",
  error: "red",
  muted: "gray",
  text: "white",
  dim: "gray",
} as const;

export function stateColor(state: string): string {
  switch (state) {
    case "DONE":
    case "confirmed":
    case "done":
    case "pass":
    case "advanced":
      return colors.ok;
    case "IN PROGRESS":
    case "active":
    case "gating":
    case "running":
    case "in_progress":
      return colors.accent;
    case "BLOCKED":
    case "fail":
    case "error":
      return colors.error;
    case "skipped":
    case "skip":
    case "warn":
      return colors.warn;
    default:
      return colors.muted; // todo / pending / unknown
    }
}

export function glyphFor(state: string): string {
  switch (state) {
    case "DONE":
    case "confirmed":
    case "done":
    case "pass":
      return "✓"; // ✓
    case "IN PROGRESS":
    case "active":
    case "gating":
    case "running":
    case "in_progress":
      return "●"; // ●
    case "BLOCKED":
    case "fail":
    case "error":
      return "✖"; // ✖
    case "skipped":
    case "skip":
      return "⊘"; // ⊘
    case "warn":
      return "⚠"; // ⚠
    default:
      return "○"; // ○
  }
}

export function kindGlyph(kind: string): string {
  switch (kind) {
    case "thinking":
      return "⁙"; // reasoning marker
    case "tool":
      return "⚙"; // ⚙
    case "result":
      return "↳"; // ↳
    case "stderr":
      return "!";
    case "system":
      return "ℹ"; // ℹ
    default:
      return "·"; // ·
  }
}
