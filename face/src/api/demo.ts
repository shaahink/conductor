import type { DataSource } from "./dataSource.js";
import type {
  CheckpointDto,
  ConductorEventDto,
  ControlAcceptedDto,
  GateDto,
  InjectAcceptedDto,
  ProcessDto,
  ProcessesDto,
  QueryResultDto,
  SessionRowDto,
  SessionsDto,
  StageDto,
  StateDto,
  TaskDto,
  TasksDto,
  TranscriptLineDto,
} from "./types.js";

// A fixture close to this repo's own CONDUCTOR-VNEXT-PLAN.md so `--demo` doubles as an honest
// illustration of the real Foreman plan rather than generic placeholder text ("Stage 1", "Task A").
const FIXTURE: Array<{ id: string; title: string; checkpoints: Array<{ id: string; title: string }> }> = [
  { id: "F0", title: "Foundations — kill list, async engine, integration harness", checkpoints: [
    { id: "F0.1", title: "Kill list executed" },
    { id: "F0.2", title: "Async control loop" },
    { id: "F0.3", title: "Integration harness" },
  ] },
  { id: "F1", title: "run.db task store + tracker-as-view + task/note verbs", checkpoints: [
    { id: "F1.1", title: "run.db schema" },
    { id: "F1.2", title: "Tracker-as-view" },
    { id: "F1.3", title: "conductor task/note verbs" },
    { id: "F1.4", title: "conductor report --query" },
  ] },
  { id: "F2", title: "ProcessSupervisor + Job Objects + bg primitives", checkpoints: [
    { id: "F2.1", title: "ProcessSupervisor + Job Objects" },
    { id: "F2.2", title: "PID registry + orphan reaper" },
    { id: "F2.3", title: "conductor bg start/status/logs/stop" },
    { id: "F2.4", title: "MCP bg surface + harness proof" },
  ] },
  { id: "F3", title: "Stall v2 + same-failure breaker + pre-flight", checkpoints: [
    { id: "F3.1", title: "Stall detection v2" },
    { id: "F3.2", title: "Soft-kill debrief" },
    { id: "F3.3", title: "Same-failure circuit breaker" },
    { id: "F3.4", title: "Pre-flight health check" },
  ] },
  { id: "F4", title: "Verifier role + scoring loop + findings-as-retry", checkpoints: [
    { id: "F4.1", title: "Verifier role" },
    { id: "F4.2", title: "Score output" },
    { id: "F4.3", title: "Retry-with-findings" },
    { id: "F4.4", title: "Advisor verdicts honored" },
    { id: "F4.5", title: "Handoff fact-check" },
  ] },
  { id: "F5", title: "Control plane — HTTP+SSE on localhost", checkpoints: [
    { id: "F5.1", title: "HTTP+SSE localhost control plane" },
    { id: "F5.2", title: "control.json verbs exposed over HTTP" },
    { id: "F5.3", title: "Headless mode unchanged; contract tests" },
  ] },
  { id: "F6", title: "Ink TUI v1 — TypeScript rebuild", checkpoints: [
    { id: "F6.1", title: "TS+Ink project scaffold + build split" },
    { id: "F6.2", title: "Plan pane" },
    { id: "F6.3", title: "Agent pane" },
    { id: "F6.4", title: "Process pane + command palette + ticker" },
    { id: "F6.5", title: "Golden-layout snapshot tests" },
  ] },
  { id: "F7", title: "Plan import + truth gates + speed program", checkpoints: [
    { id: "F7.1", title: "Plan import (LLM)" },
    { id: "F7.2", title: "Re-import diff" },
    { id: "F7.3", title: "Truth-gate tier" },
    { id: "F7.4", title: "Gate caching by SHA" },
  ] },
  { id: "F8", title: "conductor chat + Telegram v2", checkpoints: [
    { id: "F8.1", title: "conductor chat" },
    { id: "F8.2", title: "Telegram v2" },
  ] },
  { id: "F9", title: "Dogfood close", checkpoints: [
    { id: "F9.1", title: "Real Shamshir stage end-to-end" },
    { id: "F9.2", title: "Final audit" },
  ] },
];

const THINKING_LINES = [
  "Checking how the plan tree paginates a 100+ column terminal before adding the search overlay.",
  "The mouse click coordinates need to map through the same layout math the renderer used this frame.",
  "Verifying the SSE reconnect logic resumes from the last seq rather than replaying the whole log.",
  "Weighing whether tool-call folding should default open or closed for the first checkpoint of a stage.",
  "The ticker needs a stable width or it'll jitter every tick as the cost string grows a digit.",
  "Double-checking the control palette's destructive-verb confirm step matches the CLI's --force semantics.",
];

const TEXT_LINES = [
  "Reading src/components/PlanTree.tsx to see how selection state is threaded through to the palette.",
  "Wiring the process pane's last-output-line column to the /processes endpoint.",
  "Adding a golden snapshot test at 200x50 for the wide-terminal layout.",
  "Running the build to confirm the tsup bundle stays under the target incremental time.",
  "Extending the agent pane's search overlay to jump between matches with n/N.",
  "Hooking the inject editor's preview pane up to the selected target stage.",
];

const TOOL_LINES = ["npm run build", "npm test", "git commit -m 'feat: mouse support in plan tree'", "npm run typecheck"];
const RESULT_LINES = ["0 warnings, 0 errors — build finished in 6.2s", "42/42 tests passed", "bundle size: 812 KB"];

function pick<T>(arr: readonly T[]): T {
  return arr[Math.floor(Math.random() * arr.length)]!;
}

interface MutableStage {
  id: string;
  title: string;
  checkpoints: CheckpointDto[];
  attempts: number;
  lastOutcome: string;
  costUsd: number;
}

/** Fully offline data source — lets `conductor-face --demo` run and look alive with zero engine,
 * so the TUI can be reviewed without spinning up a real multi-hour orchestrator run. */
export class DemoDataSource implements DataSource {
  readonly label = "demo (offline, synthetic data)";

  private stages: MutableStage[];
  private stageIdx = 0;
  private checkpointIdx = 0;
  private status: "Running" | "Paused" | "Aborted" = "Running";
  private attentionReason: string | null = null;
  private sessionNumber = 47;
  private sessionKind: "Deliver" | "Verify" | "Fix" = "Deliver";
  private attempt = 1;
  private sessionElapsedSec = 0;
  private sessionCostUsd = 0;
  private sessionTokensIn = 0;
  private sessionTokensOut = 0;
  private sessionTokensThink = 0;
  private totalCostUsd = 18.42;
  private totalTokensIn = 812_000;
  private totalTokensOut = 240_000;
  private totalTokensThink = 96_000;
  private gates: GateDto[] = [
    { name: "build", state: "pending", elapsedSec: 0 },
    { name: "tests", state: "pending", elapsedSec: 0 },
  ];
  private processes: ProcessDto[] = [];
  private sessions: SessionRowDto[] = [];
  private injections: Array<{ content: string; stageId: string | null }> = [];
  private tick = 0;
  private timer: ReturnType<typeof setInterval> | null = null;

  private eventSubs = new Set<(evt: ConductorEventDto) => void>();
  private transcriptSubs = new Set<(line: TranscriptLineDto) => void>();
  private seq = 0;

  constructor() {
    this.stages = FIXTURE.map((s, i) => ({
      id: s.id,
      title: s.title,
      attempts: i <= 5 ? 1 : 0,
      lastOutcome: i <= 5 ? "advanced" : "",
      costUsd: i <= 5 ? Number((Math.random() * 4 + 1).toFixed(2)) : 0,
      checkpoints: s.checkpoints.map((c, ci) => ({
        id: c.id,
        title: c.title,
        // F0-F5 fully DONE, F6 partially in progress (this very work), F7-F9 TODO — mirrors reality.
        status: i < 5 ? "DONE" : i === 5 ? "DONE" : i === 6 ? (ci < 3 ? "DONE" : "TODO") : "TODO",
      })),
    }));
    this.stageIdx = 6; // F6
    this.checkpointIdx = 3; // F6.4
    this.seedProcesses();
    this.seedSessionHistory();
    this.start();
  }

  private seedProcesses() {
    this.processes = [
      { pid: 40120, purpose: "gate:build", stageId: "F6", sessionNumber: 47, startedUtc: new Date(Date.now() - 12_000).toISOString(), exitedUtc: null, exitCode: null, alive: true, lastOutputLine: null },
      { pid: 40133, purpose: "bg:tui-watch", stageId: "F6", sessionNumber: 47, startedUtc: new Date(Date.now() - 340_000).toISOString(), exitedUtc: null, exitCode: null, alive: true, lastOutputLine: "watching for changes..." },
      { pid: 39980, purpose: "gate:tests", stageId: "F5", sessionNumber: 46, startedUtc: new Date(Date.now() - 600_000).toISOString(), exitedUtc: new Date(Date.now() - 580_000).toISOString(), exitCode: 0, alive: false, lastOutputLine: null },
    ];
  }

  private seedSessionHistory() {
    const kinds = ["Deliver", "Verify", "Fix"];
    for (let n = 41; n <= 46; n++) {
      this.sessions.push({
        number: n,
        stageId: n < 44 ? "F5" : "F6",
        kind: pick(kinds),
        startedUtc: new Date(Date.now() - (47 - n) * 900_000).toISOString(),
        endedUtc: new Date(Date.now() - (47 - n) * 900_000 + 600_000).toISOString(),
        outcome: "advanced",
        attempt: 1,
        resumeCount: 0,
        gateSummary: "build pass, tests pass",
        resultSummary: "checkpoint delivered, gates green",
        commitCount: 1,
      });
    }
  }

  private start() {
    this.timer = setInterval(() => this.onTick(), 1200);
  }

  private onTick() {
    this.tick++;
    this.sessionElapsedSec += 1.2;
    this.sessionCostUsd += 0.004 + Math.random() * 0.003;
    this.sessionTokensIn += Math.floor(80 + Math.random() * 120);
    this.sessionTokensOut += Math.floor(40 + Math.random() * 80);
    this.sessionTokensThink += Math.floor(20 + Math.random() * 60);

    if (this.status !== "Running") return;

    if (this.tick % 3 === 0) {
      const kind = pick(["thinking", "text", "text", "tool"] as const);
      const text = kind === "thinking" ? pick(THINKING_LINES) : kind === "tool" ? pick(TOOL_LINES) : pick(TEXT_LINES);
      this.emitTranscript(kind, text);
      if (kind === "tool" && Math.random() < 0.5) {
        setTimeout(() => this.emitTranscript("result", pick(RESULT_LINES)), 800);
      }
    }

    if (this.tick % 6 === 0) {
      const g = this.gates[this.tick % 2 === 0 ? 0 : 1]!;
      if (g.state === "pending") g.state = "running";
      else if (g.state === "running") {
        g.state = "pass";
        g.elapsedSec = Number((2 + Math.random() * 6).toFixed(1));
      } else {
        g.state = "pending";
        g.elapsedSec = 0;
      }
      this.emitEvent({ type: "gateFinished", name: g.name, passed: g.state === "pass" });
    }

    if (this.tick % 16 === 0) {
      this.advanceCheckpoint();
    }
  }

  private advanceCheckpoint() {
    const stage = this.stages[this.stageIdx];
    if (!stage) return;
    const cp = stage.checkpoints[this.checkpointIdx];
    if (cp && cp.status !== "DONE") {
      cp.status = "DONE";
      this.emitEvent({ type: "checkpointConfirmed", checkpointId: cp.id, stageId: stage.id });
      this.sessions.unshift({
        number: this.sessionNumber,
        stageId: stage.id,
        kind: this.sessionKind,
        startedUtc: new Date(Date.now() - this.sessionElapsedSec * 1000).toISOString(),
        endedUtc: new Date().toISOString(),
        outcome: "advanced",
        attempt: this.attempt,
        resumeCount: 0,
        gateSummary: "build pass, tests pass",
        resultSummary: `${cp.id} delivered — ${cp.title}`,
        commitCount: 1,
      });
      this.totalCostUsd += this.sessionCostUsd;
      this.totalTokensIn += this.sessionTokensIn;
      this.totalTokensOut += this.sessionTokensOut;
      this.totalTokensThink += this.sessionTokensThink;
      this.sessionNumber++;
      this.sessionCostUsd = 0;
      this.sessionTokensIn = 0;
      this.sessionTokensOut = 0;
      this.sessionTokensThink = 0;
      this.sessionElapsedSec = 0;
      this.attempt = 1;
    }
    if (this.checkpointIdx < stage.checkpoints.length - 1) {
      this.checkpointIdx++;
    } else if (this.stageIdx < this.stages.length - 1) {
      this.stageIdx++;
      this.checkpointIdx = 0;
    }
  }

  private emitTranscript(kind: string, text: string) {
    const line: TranscriptLineDto = { seq: ++this.seq, ts: new Date().toISOString(), sessionId: String(this.sessionNumber), kind, text };
    for (const sub of this.transcriptSubs) sub(line);
  }

  private emitEvent(partial: Record<string, unknown>) {
    const evt: ConductorEventDto = {
      type: "info",
      seq: ++this.seq,
      ts: new Date().toISOString(),
      runId: "demo-run",
      sessionId: String(this.sessionNumber),
      ...partial,
    };
    for (const sub of this.eventSubs) sub(evt);
  }

  private currentStageDto(): StageDto[] {
    return this.stages.map((s, i) => {
      const done = s.checkpoints.filter((c) => c.status === "DONE").length;
      const total = s.checkpoints.length;
      const state =
        done === total ? "confirmed" : i === this.stageIdx ? "active" : i < this.stageIdx ? "done" : "todo";
      return {
        id: s.id,
        title: s.title,
        done,
        total,
        state,
        attempts: s.attempts,
        lastOutcome: s.lastOutcome,
        costUsd: s.costUsd,
        parentId: null,
        depth: 0,
        checkpoints: s.checkpoints,
      };
    });
  }

  async getState(): Promise<StateDto> {
    const stages = this.currentStageDto();
    const doneCount = stages.reduce((a, s) => a + s.done, 0);
    const totalCount = stages.reduce((a, s) => a + s.total, 0);
    const stage = this.stages[this.stageIdx];
    const cp = stage?.checkpoints[this.checkpointIdx];
    return {
      planName: "Foreman",
      status: this.status,
      attentionReason: this.attentionReason,
      stageId: stage?.id ?? "",
      stageTitle: stage?.title ?? "",
      persona: "deliver",
      doneCount,
      totalCount,
      totalCostUsd: this.totalCostUsd,
      overheadCostUsd: this.totalCostUsd * 0.18,
      tokensInput: this.totalTokensIn,
      tokensOutput: this.totalTokensOut,
      tokensReasoning: this.totalTokensThink,
      currentCheckpoint: cp?.id ?? "",
      currentCheckpointTitle: cp?.title ?? "",
      gateSummary: this.gates.map((g) => `${g.name}:${g.state}`).join(", "),
      stages,
      runId: "demo-run",
      repo: "C:/Code/conductor-baton",
      planDir: "plans",
      sessionNumber: this.sessionNumber,
      sessionKind: this.sessionKind,
      attempt: this.attempt,
      maxAttempts: 3,
      sessionElapsedSec: this.sessionElapsedSec,
      agentActive: this.status === "Running",
      sessionCostUsd: this.sessionCostUsd,
      sessionTokensInput: this.sessionTokensIn,
      sessionTokensOutput: this.sessionTokensOut,
      sessionTokensReasoning: this.sessionTokensThink,
      gates: this.gates,
    };
  }

  async getTasks(): Promise<TasksDto> {
    const stage = this.stages[this.stageIdx];
    if (!stage) return { tasks: [] };
    const cp = stage.checkpoints[this.checkpointIdx];
    const tasks: TaskDto[] = stage.checkpoints.slice(0, this.checkpointIdx + 1).flatMap((c, i) => [
      { taskId: `${c.id}-t1`, checkpointId: c.id, title: `Implement ${c.title.toLowerCase()}`, status: c.status === "DONE" ? "done" : "in_progress", source: "planner", order: i * 2 },
      { taskId: `${c.id}-t2`, checkpointId: c.id, title: `Test ${c.title.toLowerCase()}`, status: c.status === "DONE" ? "done" : c.id === cp?.id ? "in_progress" : "todo", source: "planner", order: i * 2 + 1 },
    ]);
    return { tasks };
  }

  async getProcesses(): Promise<ProcessesDto> {
    return { processes: this.processes };
  }

  async getSessions(): Promise<SessionsDto> {
    return { sessions: this.sessions };
  }

  async query(sql: string): Promise<QueryResultDto> {
    const lower = sql.toLowerCase();
    if (!lower.trim().startsWith("select")) {
      return { columns: [], rows: [], truncated: false, error: "only SELECT queries are allowed" };
    }
    if (lower.includes("cost")) {
      const columns = ["stage_id", "cost_usd"];
      const rows = this.stages.filter((s) => s.costUsd > 0).map((s) => ({ values: [s.id, s.costUsd.toFixed(2)] }));
      return { columns, rows, truncated: false, error: null };
    }
    if (lower.includes("sessions")) {
      const columns = ["number", "stage_id", "kind", "outcome"];
      const rows = this.sessions.map((s) => ({ values: [String(s.number), s.stageId, s.kind, s.outcome ?? ""] }));
      return { columns, rows, truncated: false, error: null };
    }
    return {
      columns: ["note"],
      rows: [{ values: ["demo mode: try a query containing 'cost' or 'sessions'"] }],
      truncated: false,
      error: null,
    };
  }

  async postControl(body: { command: string; stageId?: string }): Promise<ControlAcceptedDto> {
    switch (body.command) {
      case "pause":
      case "pause-after-stage":
        this.status = "Paused";
        return { accepted: true, reason: null };
      case "resume":
      case "approve":
        this.status = "Running";
        this.attentionReason = null;
        return { accepted: true, reason: null };
      case "abort":
        this.status = "Aborted";
        return { accepted: true, reason: null };
      case "skip":
        this.advanceCheckpoint();
        return { accepted: true, reason: null };
      case "goto": {
        const idx = this.stages.findIndex((s) => s.id === body.stageId);
        if (idx < 0) return { accepted: false, reason: `stage '${body.stageId}' not found` };
        this.stageIdx = idx;
        this.checkpointIdx = 0;
        return { accepted: true, reason: null };
      }
      case "kill":
      case "retry-stage":
      case "rollback":
      case "stop-after":
        return { accepted: true, reason: null };
      default:
        return { accepted: false, reason: `unrecognised command '${body.command}'` };
    }
  }

  async postInject(content: string, stageId?: string): Promise<InjectAcceptedDto> {
    this.injections.push({ content, stageId: stageId ?? null });
    return { accepted: true, reason: null, runId: "demo-run", stageId: stageId ?? null, recordedUtc: new Date().toISOString() };
  }

  subscribeEvents(onEvent: (evt: ConductorEventDto) => void, onConnectedChange: (c: boolean) => void): () => void {
    this.eventSubs.add(onEvent);
    onConnectedChange(true);
    return () => this.eventSubs.delete(onEvent);
  }

  subscribeTranscript(onLine: (line: TranscriptLineDto) => void, onConnectedChange: (c: boolean) => void): () => void {
    this.transcriptSubs.add(onLine);
    onConnectedChange(true);
    return () => this.transcriptSubs.delete(onLine);
  }

  dispose(): void {
    if (this.timer) clearInterval(this.timer);
    this.eventSubs.clear();
    this.transcriptSubs.clear();
  }
}
