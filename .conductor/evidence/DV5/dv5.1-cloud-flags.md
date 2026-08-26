# DV5.1 — cloud flags verified against the INSTALLED claude CLI

Trap 16 requires every DV5 checkpoint to open by verifying its cloud flags against the
installed `claude`, because the platform surface is a research preview and
`docs/dev/NEXT-ERA-FINDINGS-2026-08-23.md` predates this session by three days.

Measured 2026-08-26 on this machine.

```
$ claude --version
2.1.246 (Claude Code)
```

## The flags DV5.1 depends on — all present

Verbatim from `claude --help` (line numbers are into that help text):

```
54:  --cloud [description|session_id|url]  Create a cloud session with the given
55:                                        description, or attach to an existing
56:                                        one by session ID or claude.ai/code URL
73:  --environment <environment_id>        Create a new cloud session that runs on
74:                                        the given self-hosted environment
75:                                        (ccpool_...).
134: --output-format <format>              Output format (only works with --print):
135:                                       "text" (default), "json" (single
136:                                       result), or "stream-json" (realtime
137:                                       streaming) (choices: "text", "json",
138:                                       "stream-json")
150: -p, --print                           Print response and exit (useful for
151:                                       pipes).
188: --session-id <uuid>                   Use a specific session ID for the
189:                                       conversation (must be a valid UUID)
197: --teleport [session]                  Resume a teleport session, optionally
198:                                       specify session ID
```

### Findings-doc claims, checked one by one

| Doc claim (§2.3 CL-2, §2.4) | Verdict against installed CLI |
| --- | --- |
| `claude --cloud` creates a cloud session | **CONFIRMED** — `--cloud [description…]` |
| `-p … --cloud <id>` addresses an existing session for follow-ups | **CONFIRMED** — the same flag takes `session_id` or a `claude.ai/code` URL |
| `--teleport` is the only bridge back and needs a clean tree + pushed branch | **CONFIRMED present** as a flag; the clean-tree requirement is not restated in help text |
| Cloud sessions clone from the remote (§2.4 item 4) | **not contradicted**; help gives `--cloud` no path/worktree argument, which is consistent with a server-side clone. This is exactly why 6.8 demands the preflight. |

### What the doc did NOT know about, found here

* `--environment <ccpool_…>` — self-hosted cloud environments. Not modelled by CL-2/CL-1 and
  **not used** by DV5.1; recorded so a later era does not rediscover it.
* `--max-budget-usd <amount>` is documented as **"only works with `--print`"**. It is therefore
  *not* a dollar cap available on a `--cloud` create. This is a second, independent confirmation
  of the DV5 honesty rule: the engine has no meter for a cloud session, so its cost is `unknown`.
* `claude agents --json` lists *background* sessions (`--bg`), which is a different surface from
  `--cloud`. `claude agents --help` documents no cloud filter. There is no `claude cloud`
  subcommand — `--cloud` is a top-level flag only.

### Flags that do NOT exist (do not guess a synonym — trap 16)

Searched the full help output: there is **no** `--cloud-repo`, `--cloud-branch`, `--repo`,
`--branch`, `--cloud-session`, or `--cloud-list`. A cloud session is created from the invoking
working directory's repo state as the server sees it on the remote; conductor supplies the repo
by choosing the *working directory it spawns in*, and nothing else.

## Consequence for the implementation

1. Create:   `claude --cloud "<task>" -p --output-format json` in the project's repo directory.
2. Follow up: `claude --cloud <session-id> -p "<text>" --output-format json`.
3. No cost flag is available on either, so the reply and the event log say **unknown**, never a number.
