# Chapar — the remote surface (KS11 spec, karvansara-edge)

*2026-08-18. The chapar was the relay courier of the Achaemenid road system — the rider who carried
news between caravanserais. This document is the spec for stage KS11 of `plans/karvansara/edge.plan.json`,
the way GITHUB-SYNC-DESIGN-2026-08-13.md was the spec for KS9. It exists because the owner asked for
it by name, with a live consumer waiting: the BookToCourse run will be watched by a non-technical
stakeholder in a Telegram group chat, and today's bot cannot serve that safely or well.*

## Why now — what today's bot cannot do

Measured against the code (`src/Conductor.Core/Integrations/TelegramService.*`, 2026-08-18):

1. **Permissions are all-or-nothing per chat.** `allowedChatIds` gates commands and pushes together;
   any allowed chat gets `/inject` (a steering verb) and, under `enableTwoWay`, the control verbs.
   There is no way to put a stakeholder in a chat the bot serves without also handing them the
   steering wheel. This is the blocker: the observer role does not exist.
2. **Detail is push-capped, not pull-based.** Hard constants — `EvidenceFilesPerPush = 4`,
   `EvidenceLinesPerPush = 8`, `TelegramResultMaxChars = 900`, `CommitLinksPerPush = 3` — clip every
   push the same way for every reader. The owner's complaint "it only sends part of the evidence" is
   these four constants. Raising them would trade truncation for noise; the fix is on-demand depth.
3. **No onboarding.** A chat added mid-run receives pushes with no frame: what this run is, what will
   be reported, what may be asked. `/start` answers one static sentence.
4. **Composition is log-shaped, not evidence-shaped.** A session-end push is a status line plus
   clipped result text. It says what happened; it does not say what was delivered and what proves it.
5. **Everything is Telegram-typed.** Composition, permissions, and browsing live inside
   `TelegramService`. A future channel (Slack, a web page, anything) would re-implement all of it.

## Decisions

**CH-1 — One seam, one channel (for now).** Message composition, chat profiles, and evidence
browsing are defined channel-agnostic in a messenger seam; `TelegramService` becomes the transport
adapter behind it. The seam is proven by a fake channel in tests — **no second channel is built this
era**. Building Slack to prove an interface is how scope dies; a fake proves the same thing for free.

**CH-2 — Two profiles: `admin` and `observer`.** Config grows per-chat profiles:

```jsonc
"telegram": {
  "chats": [
    { "chatId": "99205495",   "profile": "admin"    },   // the owner, full surface
    { "chatId": "-100123456", "profile": "observer" }    // the group chat, stakeholder inside
  ]
}
```

Back-compat is byte-identical behaviour: a plan carrying only the old `allowedChatIds` list reads as
admin chats, and a plan with neither block behaves exactly as today (silent). An unknown profile
string is refused by name at plan load — never quietly read as a default (the `GithubConfig.Board`
rule, reused).

**CH-3 — The observer capability set is closed, and enforced at dispatch.** Observers receive the
run's story (checkpoint/stage/park/run-end pushes with progress, money, tokens) and may **browse**:
`/status`, `/tasks`, `/progress`, `/evidence`, `/daily`. Nothing else — no `/inject`, no control
verbs, no approval. A control attempt from an observer chat gets a one-line named refusal, and the
test for this is an exhaustive command-x-profile matrix, not a sample. Admin keeps today's full
surface. Group-chat mechanics (Telegram privacy mode: a bot in a group sees only commands addressed
to it unless privacy is off) are documented in operating.md as part of this checkpoint.

**CH-4 — Onboarding is the bot's first message, per profile.** At run start (and on `/start`), each
configured chat receives an onboarding message in its profile's voice: what this run is (plan name,
stage map, budget ceiling), what will be pushed here and when, and exactly what this chat can ask
for. The observer version reads as a welcome to a project dashboard; the admin version includes the
control surface. No chat should ever receive its first push without having been told the rules.

**CH-5 — Pushes read like evidence, not logs.** One visual grammar across all pushes: a headline
line (what landed), a proof line (what shows it — gate verdict, evidence artifact name), and a
telemetry line (progress n/N, money spent vs cap, tokens) in monospace. HTML formatting, consistent
across event types, readable standalone on a phone. The four clip constants stop being the depth
mechanism: pushes carry the headline; depth is pulled (CH-6). Renderings are pinned by goldens for
both profiles.

**CH-6 — Depth on demand.** `/evidence` lists the checkpoints that have evidence; `/evidence <id>`
sends the artifact — as a document upload when it is a file, as chunked text otherwise (HtmlChunker
already exists), with size caps and a per-chat rate limit. `/progress`, `/money`, `/tokens` answer
with figures that cross-check against `conductor status` and `money` on the same run.db — billed
money only, never a price table. This is the answer to "all the details, all evidence, or just
checkpoint with cost": the push tier is the checkpoint-with-cost view; the command tier is the rest.
One honest caveat, said in docs rather than solved in code: evidence files are served as-is; what a
group chat may see is the owner's call when they grant the profile, not a redaction layer this era.

**CH-7 — The GitHub mirror is the other observer surface, already shipped.** KS9's issue board plus
`REPORT.md` cover the stakeholder who prefers a browser; nothing in KS11 duplicates it. A plan opts
in with the `github` block and the engine mirrors automatically — no hand-created issues, ever.

**CH-8 — Explicitly not this era.** Slack/Discord/WhatsApp adapters; Telegram Mini Apps; a web
dashboard; inbound anything beyond the closed command set (ADR 0005 spirit — the 2026 MCP/webhook
attack record has not improved); GitHub Pages for run evidence (public on every plan below
Enterprise Cloud — rejected, not deferred).

## KS11 checkpoints (falsifiable exits)

| cp | Work | Falsifiable exit |
|----|------|------------------|
| KS11.1 | The messenger seam: composition/profiles/browsing extracted channel-agnostic; TelegramService becomes the adapter | Golden replay proves current pushes byte-identical through the seam; a fake channel drives the full surface in tests; architecture test forbids Telegram types outside the adapter |
| KS11.2 | Profiles admin/observer per chat, back-compat for `allowedChatIds` | Old-shape plans behave byte-identically (pinned); observer control attempt refused by name; command-x-profile matrix test exhaustive |
| KS11.3 | Onboarding + the push grammar (headline / proof / telemetry) | Run start posts per-profile onboarding; goldens pin both profiles' renderings of every push type; a checkpoint push carries what-landed + what-proves-it + cost and reads standalone |
| KS11.4 | Evidence on demand: list + fetch with document upload, size caps, per-chat rate limit | An observer pulls a real evidence artifact end-to-end in the rig; the four clip constants no longer bound what a reader can reach |
| KS11.5 | Metrics on demand (`/progress`, `/money`, `/tokens`) + digest re-rendered in the grammar | Figures cross-check against `status`/`money` on the same run.db to the cent; digest golden pinned |

## The early-ship option (why KS11 carries an ownerGate)

KS11 leads the edge plan and parks when its checkpoints are confirmed. The park is the owner's
window to reinstall the mid-era engine so the BookToCourse run (a separate run sharing this machine)
picks up the observer surface at its next session boundary — pause that run, reinstall, resume both.
Taking the option or skipping it is the owner's call; the park exists so the choice is offered
rather than foreclosed by trap 1 ("never reinstall mid-run").

## Live-proof rules for this stage

All Telegram proofs run against a **scratch bot token and scratch chats** — never the owner's real
chat, never the BookToCourse group. `TelegramConfig.ApiBaseUrl` is the test seam (stand a stub,
assert the wire). The engine's own live mirror handles the GitHub board; no session posts issues by
hand.
