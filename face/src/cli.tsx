import React from "react";
import { render } from "ink";
import { App } from "./App.js";
import { Store } from "./state/store.js";
import { StoreProvider } from "./state/context.js";
import { LiveDataSource } from "./api/dataSource.js";
import { DemoDataSource } from "./api/demo.js";
import { enableMouse, disableMouse } from "./input/mouse.js";

const HELP = `conductor-face — F6 Ink TUI for the Conductor control plane

Usage:
  conductor-face [--url <base>] [--demo] [--host <ip>] [--port <n>]

Options:
  --url <base>   Full control-plane base URL (default http://127.0.0.1:4317)
  --host <ip>    Host only, combined with --port (default 127.0.0.1)
  --port <n>     Port only, combined with --host (default 4317)
  --demo         Run fully offline against synthetic data — no conductor process needed.
                 Everything (plan tree, agent transcript, processes, sessions, palette,
                 inject, report) is interactive so you can review the whole UI cold.
  -h, --help     Show this help and exit

Requires --control-plane on the conductor side for live mode:
  conductor run -p <plan> --control-plane [--control-plane-port <n>]
`;

function parseArgs(argv: string[]) {
  let url: string | null = null;
  let host = "127.0.0.1";
  let port = 4317;
  let demo = false;
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--demo") demo = true;
    else if (a === "--url") url = argv[++i] ?? null;
    else if (a === "--host") host = argv[++i] ?? host;
    else if (a === "--port") port = Number(argv[++i] ?? port);
    else if (a === "-h" || a === "--help") {
      process.stdout.write(HELP);
      process.exit(0);
    }
  }
  return { demo, url: url ?? `http://${host}:${port}` };
}

async function main() {
  const { demo, url } = parseArgs(process.argv.slice(2));

  if (!process.stdout.isTTY && !process.env["FACE_FORCE_TTY"]) {
    process.stderr.write("conductor-face needs an interactive terminal (stdout is not a TTY).\n");
    process.exitCode = 1;
    return;
  }

  const store = new Store(demo ? "demo" : "live", url);
  const source = demo ? new DemoDataSource() : new LiveDataSource(url);

  let cleanedUp = false;
  const cleanup = () => {
    if (cleanedUp) return;
    cleanedUp = true;
    try {
      disableMouse(process.stdout);
    } catch {
      /* best effort */
    }
    source.dispose();
  };

  // A crash in the TUI must never look like the conductor run died — it's a separate process
  // talking over HTTP; the worst case here is "the view goes away", never "the run stops".
  process.on("uncaughtException", (err) => {
    cleanup();
    process.stderr.write(`conductor-face crashed: ${err.stack ?? err.message}\n`);
    process.stderr.write("The conductor run (if any) is unaffected — it only talks to this TUI over HTTP.\n");
    process.exit(1);
  });
  process.on("unhandledRejection", (err) => {
    cleanup();
    process.stderr.write(`conductor-face crashed (unhandled rejection): ${String(err)}\n`);
    process.exit(1);
  });
  process.on("exit", cleanup);
  process.on("SIGINT", () => {
    cleanup();
    process.exit(0);
  });

  enableMouse(process.stdout);

  const instance = render(
    <StoreProvider store={store}>
      <App source={source} />
    </StoreProvider>,
  );

  await instance.waitUntilExit();
  cleanup();
}

main();
