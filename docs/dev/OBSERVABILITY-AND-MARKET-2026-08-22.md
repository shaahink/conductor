# Where Conductor sits, and why observability is the thing that hurts

*2026-08-22. Commissioned by the owner after months of daily use: "what's on the market, what can we
do given the nature of the tool — and separately, observability, because Telegram was good and
GitHub issues is not what I want to look at." Written against the tree at `feat/karvansara-edge`
(HEAD `870786f`, edge run 23/24, $324.01), the live `.conductor/` state, the GitHub repo's actual
issue list, and a market sweep of August 2026. Claims from this machine are marked **measured**;
claims from the sweep carry their source.*

---

## Part 1 — The market, and what it says about the nature of this tool

### 1.1 The orchestrator lane is crowded and commoditising

Nine-plus open-source orchestrators now do "many coding agents, git worktrees, a kanban": Agent
Orchestrator, Emdash, Baton, three unrelated products called Conductor (conductor.build the macOS
app, Code Conductor, Microsoft Conductor), Bernstein, Claude Squad, Crystal, Vibe Kanban, Agent
Kanban. Bloop — the company behind Vibe Kanban, the closest thing to a category leader — **shut down
on 2026-04-10**; the project survives community-maintained with its cloud half (issues, comments,
projects, orgs) removed.

That is simultaneously proof the demand is real and proof the shape is not defensible. *Parallel
agents in worktrees with a board* is a feature, not a company.

### 1.2 The platforms have absorbed the session layer

The decisive fact, and it is new since this repo last looked:

- **Claude Code** ships `/remote-control` (drive a local session from phone/web), `--teleport` (move
  a cloud session to the terminal and back), and **Agent View** (shipped 2026-05-11), a dashboard
  for parallel sessions. Sessions sync across terminal, desktop, phone and browser.
- **GitHub Agent HQ** with **mission control** — assign tasks to agents across repos, pick a custom
  agent, watch real-time session logs, steer mid-run (pause / refine / restart), consistent across
  github.com, VS Code, mobile and the Copilot CLI, with a governance control plane and a metrics
  dashboard. Agents from Anthropic, OpenAI, Google, Cognition and xAI land inside it.

**Implication, stated plainly: do not build "watch and steer one agent from my phone."** It is table
stakes, owned by the vendors, bundled into subscriptions already paid for, and it will be better
than anything this repo can staff.

### 1.3 What is *not* commoditised: not believing the agent

The research caught up with this repo's thesis this year:

- **SpecBench** (arXiv 2605.21384, 2026-05-21) defines the **Reward Hacking Gap Δ** = visible-test
  pass rate − held-out-test pass rate, measured across Codex, Claude Code and OpenCode. Finding:
  *every model saturated the visible suite on every task*, and Δ grows with task complexity. Weaker
  models hack harder; no model is clean.
- **The Verification Horizon** (2606.26300): no silver bullet for coding-agent rewards.
- *Do Coding Agents Deceive Us?* (2606.07379), EvilGenie, Hodoscope — a whole 2026 literature on
  detecting specification gaming, and a consensus that holdout splits alone do not remediate it.
- Gartner: **40% of CIOs will demand "guardian agents" by 2028.** The verification layer is being
  named as a category: agent gateways, eval pipelines, and agents that contain other agents.

Conductor shipped the product form of that literature at KS4, before the papers were mainstream:

| The research asks for | This repo has, shipped |
|---|---|
| Held-out tests the agent cannot see | **KS4.1** holdout gate class — excluded from prompts, tool contract and agent-readable logs; grep of the composed prompt *and* the transcript proves absence; a seeded gaming fake-agent passes visible gates and fails holdout |
| PASS-TO-PASS / nothing-that-worked-broke | **KS4.2** regression gate class with distinct reporting |
| Tests that are not themselves gamed | **KS4.3** mutation gate, diff-scoped, Stryker.NET |
| A judge that cannot become the referee | **KS4.5** judge as *evidence, never verdict* — with a test asserting no code path lets a judge score flip a gate |
| Clean-room attribution of the attempt | **KS4.4** worktree-per-stage-attempt; a failed attempt drops the tree |

Nobody else has this assembled, and it is the one thing a platform vendor is structurally bad at:
**the vendor selling you the agent cannot credibly sell you the referee that disbelieves it.**

### 1.4 Spec formats consolidated — be the runner, not another format

OpenSpec (52.1k stars, on the Thoughtworks Radar), GitHub Spec Kit, AWS Kiro, BMAD, Tessl, Google
Antigravity, Backlog.md — by 2026 every major tool shipped a spec-driven flavour. This repo's
posture is already correct and should be held: **KS3.5** imports spec-kit `tasks.md`, Task-Master
`tasks.json` and plain markdown checklists with no model call, and `conductor demo --from <file>`
drives someone else's board before they install anything. Do not compete on the spec format. Be the
thing that *runs and verifies* whichever format wins.

Likewise **OTel GenAI semantic conventions** (v1.41 defines agent, workflow, tool and model spans;
the sweep's guidance is "make OTel support a hard buying requirement"). KS7.3 already emits it —
a checkbox competitors will need and this repo has.

### 1.5 Positioning, in one line

> Conductor is not a way to run many agents. It is **the referee** — the thing that re-runs your
> gates itself, hides some of them from the agent, and prices every checkpoint to the cent.

Three assets no competitor holds together: independent gate re-run after every session; gates the
agent cannot see; billed-money and token truth per checkpoint across eighteen published runs
**including the failures**.

**The sharpest single thing that could ship next: `conductor verify` — a Reward-Hacking Gap report
for your own repo.** Run a plan, print Δ per checkpoint: what passed the visible battery, what the
holdout class said, where they disagreed, and what it cost. SpecBench measures that number about
models in a lab. Nobody can print it about *your* codebase. That is a headline, a benchmark and a
reason to install — and the machinery all exists already (KS4.1's holdout class, KS6.4's pure
evidence-to-verdict function, the evidence taxonomy).

---

## Part 2 — Observability: what is measured, and why it hurts

### 2.1 What exists today

| Surface | State | Reach |
|---|---|---|
| **Telegram / Chapar (KS11)** | admin + observer profiles, per-run onboarding, headline/proof/telemetry push grammar, `/status /tasks /progress /money /tokens /evidence /daily` on demand | phone, anywhere — **works** |
| **GitHub mirror (KS9)** | one-way reconciler over the event cursor; issues + `conductor:status:*` labels + milestones; run-diary issue with a comment per session | browser — **but see 2.2** |
| **Loopback control plane** | a complete REST surface: Tasks, Evidence, Sessions, State, OwnerQueue, Processes, Knowledge, Plan, Control, Telegram | **localhost only** |
| **REPORT.md** | committed and pushed every boundary; heartbeat writes it mid-session | the repo, on GitHub |
| **OWNER-QUEUE.md** | regenerated every boundary — the agent inbox, and the best thing here | **the machine only** |
| **MCP read surface (KS8.1)** / **ATIF export (KS8.2)** / **OTel (KS7.3)** | shipped | machines |

### 2.2 Why GitHub "doesn't work the way I want" — three causes, all measured

**Cause 1 — the edge run never reached GitHub at all.** From `.conductor/conductor.log`:

```
[19:09:25] github mirror off: enabled in the plan but no token — no GitHub token. nothing was contacted.
[19:17:03] github mirror off: enabled in the plan but no token — no GitHub token. nothing was contacted.
```

`plans/karvansara/edge.plan.json` sets `"enabled": true, "liveMirror": true, "runHistoryIssue":
true`. The run posted **zero** issues. Every one of the 33 issues on `shaahink/conductor` belongs to
the *core* run. Twenty-four checkpoints, twenty-three sessions, $324 of work — none of it on the
board the plan asked for.

The channel died silently and the failure went to a log file: not to REPORT.md, not to the owner
queue, not to Telegram, not to the run's exit status. **An observability channel that fails
invisibly is worse than one that was never configured**, because the plan says it is on and the
operator believes the plan. Highest-value, lowest-cost fix in this document.

**Cause 2 — there is no board, only a list.** KS9.3 (Projects v2, the *columns*) was honestly
SKIPPED because the token lacked `project` scope. Re-checked today:

```
Token scopes: 'delete_repo', 'gist', 'read:org', 'repo', 'user', 'workflow'
```

Still missing. Without Projects v2 the mirror is issues + labels + milestones, which GitHub renders
as a **list**. The columns — the actual Kanban, the thing that was asked for — have never existed.
`gh auth refresh -s project` is a one-time, one-command unblock.

**Cause 3 — a finished run's board is a graveyard.** 33 issues, **32 closed**. Issues model work
items with a lifecycle; when a run completes every card closes and the mirror reads as empty. That
is *correct behaviour* and the *wrong artifact* for "show me where my project is."

The conclusion worth internalising: **GitHub is an excellent archive and a bad dashboard. Telegram
is an excellent dashboard and a bad archive.** Stop trying to make either be both. The split is not
the defect; the defect is that neither is currently doing its own job well.

### 2.3 Why the bugs it finds don't get out

Measured:

- `.conductor/followups.md` — **100 KB, 262 table rows, 11 still OPEN**. Read by `FollowupParser`,
  turned into Tier-B fix lanes by `LaneCoordinator`, and otherwise seen by nobody.
- run.db carries a `bugs` table; `OpenBugsReport` counts this-run and carried-in bugs, and a test is
  literally named `SF04BugsOutliveTheirRunTests` — the data already survives the run.
- Neither reaches GitHub, and Telegram sees them only at run end.

The ledger is real, durable and invisible. Three ways out, cheapest first:

**(a) Bugs and followups become their own issue class** — `conductor:bug` / `conductor:followup`,
distinct from checkpoint issues, opened when filed and closed by the commit that closes them,
**surviving the run that found them**. Unlike checkpoints, these *stay open*. One change fixes both
complaints: found bugs get out, and the issue list stops being a graveyard and becomes a backlog
worth opening.

**(b) SARIF → GitHub code scanning** for any bug carrying a file and line. They become code-scanning
alerts: filterable, dismissable, shown on the PR diff, present in the GitHub mobile app, with their
own permanent tab that is not the issue list. Caveat from the sweep: private repos need GitHub Code
Security / Advanced Security; **public repos get it free — `shaahink/conductor` is public, so this
costs nothing here**, and the caveat needs stating in docs for anyone running private.

**(c) The daily digest gains a ledger line** — "N open bugs, M open followups, oldest is X days."
The digest exists and is golden-pinned; this is one line.

### 2.4 The hosted page, the reverse proxy, and the thing that is actually wrong

ADR-0005 rules out inbound: no port, no tunnel, no reverse proxy. The 2026 record supports holding
it — the MCP and webhook attack surface did not improve, and the loopback control plane carries
`/control`, so an inbound path to it is an inbound path to the steering wheel. Cloudflare Tunnel and
Tailscale Funnel are outbound-only *at the connector*, which is exactly the argument that makes them
feel safe, and they still terminate a public inbound route on the laptop. Not worth it for a read
view.

**The shape that fits the posture: publish, don't serve.** Every contract needed already exists in
`Http/Contracts/` — Tasks, Evidence, Sessions, OwnerQueue, State. Render the board to **one
self-contained HTML file** at each boundary — columns, cards, age-in-column, cost, evidence links,
open bugs — and push it outward:

- as a **Telegram document** (works today, zero infrastructure, Telegram stores and re-serves it),
  and/or
- to a static host already owned — the **payesh / Vercel pattern is proven in this repo**, figures
  recomputed from the run store and anonymisation failing closed — on a private or
  password-protected deployment, *not* Pages (already rejected: public below Enterprise).

Stale by one boundary, no inbound anything, survives the machine being off, and it is the same
artifact whether read now or in five years.

**But "I lose track" is not solved by any page.** That is the real complaint and it deserves the
honest answer: a dashboard requires you to go look, and you will not. The pattern the market
converged on in 2026 is the **agent inbox** — the system raises exceptions to the human instead of
the human polling a dashboard; agents escalate on low confidence, missing context or a policy
boundary rather than guessing.

The best agent inbox in this document is already built and cannot leave the machine.
`.conductor/OWNER-QUEUE.md`, regenerated every boundary, right now says:

> **3 items need you.** Most urgent first. … *Why you:* answer it, delete the HUMAN: line, then
> resume. *Clears with:* `conductor resume`

Every item carries what it unblocks, its age, why it needs a human, and **the exact command that
clears it**. That is the entire product of an "agent inbox," already written, sitting in a file on
one laptop.

> **Push the owner queue to Telegram on change, with one tappable command per item, and the
> dashboard stops being necessary.** The board answers *how is it going*. The queue answers *what do
> I have to do* — and only the second one has a deadline.

### 2.5 Ranked, with honest cost

| # | Move | Size | Why it is here |
|---|---|---|---|
| 1 | **Channel health is loud** — preflight refuses (or the run parks) on a configured-but-dead channel; per-channel state in the REPORT.md header, `/status`, and the owner queue | S | The edge run's entire GitHub record was lost to two log lines nobody read |
| 2 | **Owner queue → Telegram on change**, one tap per clearing command | S | Kills "I lose track" without building a dashboard |
| 3 | `gh auth refresh -s project`, then finish KS9.3 | S | The columns. The actual Kanban mirror, unblocked by one command |
| 4 | **Bugs + followups as a long-lived issue class** | M | Found bugs get out; the board stops being a graveyard |
| 5 | **Board snapshot as one self-contained HTML file**, pushed to Telegram and/or a private static host | M | The page that was wanted, with no inbound port |
| 6 | **SARIF → code scanning** for file/line bugs | M | Free on this public repo; a permanent, filterable, mobile-visible surface |
| 7 | **`conductor verify` — the Reward-Hacking Gap report** | L | The strategic one. See 1.5 |

### 2.6 What not to build

- **A second messenger adapter.** CH-1 was right: a fake channel proves the seam for free. Slack
  costs an era and proves nothing.
- **A remote-control TUI, or anything that steers a session from a phone.** Claude Code Remote
  Control and Agent HQ mission control own this, bundled, and are better.
- **A reverse proxy or tunnel to the control plane.** ADR-0005 holds; publish instead of serve.
- **Two-way GitHub sync.** D-7, A16 and ADR-0005 already say no, three separate times.

---

## Sources for the market half

- Gartner, *Enterprise AI Coding Agents: 2026 Market Guide* — https://www.gartner.com/en/articles/enterprise-ai-coding-agent-market
- Augment Code, *9 Open-Source Agent Orchestrators for AI Coding (2026)* — https://www.augmentcode.com/tools/open-source-agent-orchestrators
- Nimbalyst, *Best Multi-Agent Coding Tools (2026)* — https://nimbalyst.com/blog/best-multi-agent-coding-tools-2026/
- GitHub, *Introducing Agent HQ* — https://github.blog/news-insights/company-news/welcome-home-agents/ · *How to orchestrate agents using mission control* — https://github.blog/ai-and-ml/github-copilot/how-to-orchestrate-agents-using-mission-control/
- Claude Code remote control / teleport / Agent View — https://code.claude.com/docs/en/remote-control · https://www.explainx.ai/blog/claude-code-mobile-remote-control-phone-guide-2026
- *SpecBench: Measuring Reward Hacking in Long-Horizon Coding Agents* — https://arxiv.org/abs/2605.21384
- *The Verification Horizon: No Silver Bullet for Coding Agent Rewards* — https://arxiv.org/pdf/2606.26300
- *Do Coding Agents Deceive Us?* — https://arxiv.org/pdf/2606.07379
- Zylos, *Specification Gaming and Reward Hacking in Autonomous AI Agents* — https://zylos.ai/research/2026-06-07-specification-gaming-reward-hacking-ai-agents/
- The Hacker News, *Guardian Agents: The Next Layer of Identity Governance* — https://thehackernews.com/2026/06/guardian-agents-next-layer-of-identity.html
- Thoughtworks Radar, *OpenSpec* — https://www.thoughtworks.com/en-us/radar/tools/openspec
- MarkTechPost, *Top LLM Observability and Evaluation Platforms in 2026* — https://www.marktechpost.com/2026/08/09/top-llm-observability-and-evaluation-platforms-in-2026-langfuse-langsmith-braintrust-arize-and-more-compared/
- GitHub Docs, *Uploading a SARIF file to GitHub* — https://docs.github.com/en/code-security/how-tos/find-and-fix-code-vulnerabilities/integrate-with-existing-tools/upload-sarif-file
- Atlassian, *Human-in-the-loop patterns for AI agents in Jira* — https://www.atlassian.com/software/jira/guides/agentic-engineering/human-in-the-loop
- happier.dev, *Cloudflare Tunnel vs Tailscale Funnel vs ngrok* — https://guides.happier.dev/cloudflare-tunnel-vs-tailscale-funnel-vs-ngrok-for-claude-code-codex-opencode
