// Central keybinding table (D11: "command palette replacing the 20-key chord bar"). Navigation and
// pane-focus stay direct single-key bindings — everything else (the 11 control verbs, inject,
// prompt editor, session history, report) lives behind the palette so this table never grows back
// into a chord wall.

export interface KeyHelp {
  key: string;
  description: string;
}

export const GLOBAL_KEYS: KeyHelp[] = [
  { key: "Tab", description: "Cycle focused pane (plan / agent / process)" },
  { key: "1 2 3", description: "Jump directly to plan / agent / process pane" },
  { key: ": or Ctrl+K", description: "Open the command palette" },
  { key: "i", description: "Open the inject editor" },
  { key: "e", description: "Open the prompt/persona template editor" },
  { key: "h", description: "Open the session history browser" },
  { key: "r", description: "Open the report / query console" },
  { key: "?", description: "Toggle this help" },
  { key: "q or Ctrl+C", description: "Quit the Face (never touches the running conductor)" },
];

export const PLAN_PANE_KEYS: KeyHelp[] = [
  { key: "Up/Down or j/k", description: "Move selection" },
  { key: "Left/Right", description: "Collapse / expand a stage" },
  { key: "Enter", description: "Toggle expand on the selected stage" },
];

export const AGENT_PANE_KEYS: KeyHelp[] = [
  { key: "PageUp/PageDown", description: "Scroll transcript" },
  { key: "l", description: "Jump back to the live tail" },
  { key: "/", description: "Search the transcript" },
  { key: "n / N", description: "Next / previous search match" },
  { key: "f", description: "Toggle tool-call folding" },
];

export const PROCESS_PANE_KEYS: KeyHelp[] = [{ key: "Up/Down", description: "Select a process" }];
