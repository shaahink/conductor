# Security

## Reporting a vulnerability

Please **do not** open a public issue. Report privately through GitHub's
[Report a vulnerability](https://github.com/shaahink/conductor/security/advisories/new) form on this
repository, which creates a private advisory only the maintainer can see.

Include what you ran, what you observed, and — if you have one — a minimal reproduction. You will
get an acknowledgement within a few days. This is a single-maintainer hobby-scale project, so please
size your expectations accordingly: there is no SLA, and there is no bounty.

Fixes land on the default branch. There are no long-lived release branches to backport to.

## What Conductor is, in threat-model terms

Conductor exists to run an AI coding agent unattended, against your repository, with the permission
prompts turned off. That is the product, not a misconfiguration. Everything below follows from it.

**Run Conductor only on repositories and plans you trust, on a machine you control.** Treat a
conductor run as equivalent to giving a shell to whoever authored the plan and the agent backend.

### The agent has your machine

The shipped plans invoke agent CLIs with flags like `--dangerously-skip-permissions` so a session
can proceed without a human at the keyboard. The agent can therefore read, write, and delete files,
run arbitrary commands, and reach the network with your credentials. Conductor does not sandbox it —
git history and the gate battery are the recovery mechanism, not a containment boundary.

### The plan file is executable

`conductor.plan.json` is not inert configuration. It carries shell commands (`gates[].command`,
`preHook`/`postHook`, `setup`/`teardown`, `notify.command`, `advisor.remediationScript`) that
Conductor executes. **Opening someone else's plan file and running it is running their code.** Read
a plan before you drive it, the same way you would read a `Makefile` from a stranger.

The same applies to `conductor plan import` and `conductor init --from-idea`: they turn a document —
possibly model-generated — into a plan. Review the result before the first session.

### The control plane

Each run starts an HTTP control plane bound to `127.0.0.1` only, on an ephemeral port.

- **Writes are authenticated.** Every `POST` must carry a per-run random token in
  `X-Conductor-Token`. The token is published only through the run's discovery file, and that
  file's filesystem permissions are the trust boundary. This is what stops a web page you have open
  from driving your run by CSRF — browsers will happily `POST` to loopback, and `POST /inject`
  feeds text straight into the next session's prompt while `POST /plan/edit` can plant a gate
  command.
- **Reads are open**, deliberately: they are loopback-only, and a browser cannot read a cross-origin
  response without CORS headers, which this server never sends.
- Anyone who can read your user profile directory can read the token and therefore drive the run.
  On a shared machine, that is the exposure.

### Prompt injection

Content the agent reads — repository files, task-card text, imported plan documents, injected
instructions — is untrusted input to a model. Conductor frames card text as data when asking the
advisor to split it, and the advisor's answers are validated against a fixed vocabulary of actions
rather than executed as free text. This reduces the blast radius; it does not eliminate the class.
Do not point Conductor at a repository whose contents you would not paste into a model yourself.

### Secrets

Conductor does not store API keys. It relies on the agent CLI's own authentication (`claude setup-token`
and equivalents) and reads `CONDUCTOR_TELEGRAM_TOKEN` from the environment when Telegram is enabled.

Things that *can* leak secrets, and are worth knowing about:

- `.conductor/logs/session-*.jsonl` records the raw agent stream, and `session-*.prompt.md` records
  the exact prompt. If a session read a secret, it is in those files. `.conductor/`'s own
  `.gitignore` keeps everything except `REPORT.md` out of the repository, but the files are on disk
  in plaintext.
- `.conductor/REPORT.md` **is committed and pushed** by default (`report.commit` / `report.push`).
  It contains stage names, session outcomes, costs, and gate results. Turn it off for a private plan
  in a public repo.
- Gate command output is embedded verbatim into fix-session prompts.

### Telegram

Two-way Telegram control is opt-in and off by default. When enabled, `telegram.allowedChatIds`
restricts who may issue commands; leaving it empty makes the integration push-only. An empty list is
not an allow-all.

## Supported versions

The default branch is the only supported version.
