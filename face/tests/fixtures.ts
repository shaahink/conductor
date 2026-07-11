import type { DataSource } from "../src/api/dataSource.js";
import type {
  ConductorEventDto,
  ControlAcceptedDto,
  InjectAcceptedDto,
  ProcessesDto,
  QueryResultDto,
  SessionsDto,
  StateDto,
  TasksDto,
  TranscriptLineDto,
} from "../src/api/types.js";

export const FIXTURE_STATE: StateDto = {
  planName: "Foreman",
  status: "Running",
  attentionReason: null,
  stageId: "F6",
  stageTitle: "Ink TUI v1 — TypeScript rebuild",
  persona: "deliver",
  doneCount: 26,
  totalCount: 40,
  totalCostUsd: 18.42,
  overheadCostUsd: 3.31,
  tokensInput: 812_000,
  tokensOutput: 240_000,
  tokensReasoning: 96_000,
  currentCheckpoint: "F6.4",
  currentCheckpointTitle: "Process pane + command palette + ticker",
  gateSummary: "build:pass tests:running",
  runId: "run-fixture",
  repo: "C:/Code/conductor-baton",
  planDir: "plans",
  sessionNumber: 47,
  sessionKind: "Deliver",
  attempt: 1,
  maxAttempts: 3,
  sessionElapsedSec: 245,
  agentActive: true,
  sessionCostUsd: 0.42,
  sessionTokensInput: 12_000,
  sessionTokensOutput: 4_000,
  sessionTokensReasoning: 2_000,
  gates: [
    { name: "build", state: "pass", elapsedSec: 6.2 },
    { name: "tests", state: "running", elapsedSec: 3.1 },
  ],
  stages: [
    {
      id: "F5",
      title: "Control plane — HTTP+SSE on localhost",
      done: 3,
      total: 3,
      state: "confirmed",
      attempts: 2,
      lastOutcome: "advanced",
      costUsd: 4.1,
      parentId: null,
      depth: 0,
      checkpoints: [
        { id: "F5.1", title: "HTTP+SSE localhost control plane", status: "DONE" },
        { id: "F5.2", title: "control.json verbs exposed over HTTP", status: "DONE" },
        { id: "F5.3", title: "Headless mode unchanged; contract tests", status: "DONE" },
      ],
    },
    {
      id: "F6",
      title: "Ink TUI v1 — TypeScript rebuild",
      done: 3,
      total: 5,
      state: "active",
      attempts: 1,
      lastOutcome: "",
      costUsd: 2.4,
      parentId: null,
      depth: 0,
      checkpoints: [
        { id: "F6.1", title: "TS+Ink project scaffold + build split", status: "DONE" },
        { id: "F6.2", title: "Plan pane", status: "DONE" },
        { id: "F6.3", title: "Agent pane", status: "DONE" },
        { id: "F6.4", title: "Process pane + command palette + ticker", status: "TODO" },
        { id: "F6.5", title: "Golden-layout snapshot tests", status: "TODO" },
      ],
    },
    {
      id: "F7",
      title: "Plan import + truth gates + speed program",
      done: 0,
      total: 4,
      state: "todo",
      attempts: 0,
      lastOutcome: "",
      costUsd: 0,
      parentId: null,
      depth: 0,
      checkpoints: [
        { id: "F7.1", title: "Plan import (LLM)", status: "TODO" },
        { id: "F7.2", title: "Re-import diff", status: "TODO" },
        { id: "F7.3", title: "Truth-gate tier", status: "TODO" },
        { id: "F7.4", title: "Gate caching by SHA", status: "TODO" },
      ],
    },
  ],
};

export const FIXTURE_TASKS: TasksDto = {
  tasks: [
    { taskId: "F6.4-t1", checkpointId: "F6.4", title: "Implement process pane", status: "in_progress", source: "planner", order: 0 },
    { taskId: "F6.4-t2", checkpointId: "F6.4", title: "Implement command palette", status: "todo", source: "planner", order: 1 },
  ],
};

// Fixed timestamp so golden snapshots are deterministic across runs.
const FIXED_TS = "2026-07-11T02:04:43.000Z";

export const FIXTURE_PROCESSES: ProcessesDto = {
  processes: [
    { pid: 40120, purpose: "gate:build", stageId: "F6", sessionNumber: 47, startedUtc: FIXED_TS, exitedUtc: null, exitCode: null, alive: true, lastOutputLine: null },
    { pid: 40133, purpose: "bg:tui-watch", stageId: "F6", sessionNumber: 47, startedUtc: FIXED_TS, exitedUtc: null, exitCode: null, alive: true, lastOutputLine: "watching for changes..." },
  ],
};

export const FIXTURE_SESSIONS: SessionsDto = {
  sessions: [
    { number: 46, stageId: "F5", kind: "Deliver", startedUtc: FIXED_TS, endedUtc: FIXED_TS, outcome: "advanced", attempt: 1, resumeCount: 0, gateSummary: "build pass, tests pass", resultSummary: "F5.3 delivered", commitCount: 1 },
  ],
};

export const FIXTURE_TRANSCRIPT: TranscriptLineDto[] = [
  { seq: 1, ts: FIXED_TS, sessionId: "47", kind: "thinking", text: "Checking how the plan tree paginates a wide terminal." },
  { seq: 2, ts: FIXED_TS, sessionId: "47", kind: "text", text: "Reading src/components/PlanTree.tsx." },
  { seq: 3, ts: FIXED_TS, sessionId: "47", kind: "tool", text: "npm run build" },
  { seq: 4, ts: FIXED_TS, sessionId: "47", kind: "result", text: "0 warnings, 0 errors" },
];

/** A deterministic DataSource test double — no ticking, no randomness, fixed data every call. */
export class StaticDataSource implements DataSource {
  readonly label = "static-fixture";
  async getState(): Promise<StateDto> {
    return FIXTURE_STATE;
  }
  async getTasks(): Promise<TasksDto> {
    return FIXTURE_TASKS;
  }
  async getProcesses(): Promise<ProcessesDto> {
    return FIXTURE_PROCESSES;
  }
  async getSessions(): Promise<SessionsDto> {
    return FIXTURE_SESSIONS;
  }
  async query(): Promise<QueryResultDto> {
    return { columns: ["stage_id", "cost_usd"], rows: [{ values: ["F6", "2.40"] }], truncated: false, error: null };
  }
  async postControl(): Promise<ControlAcceptedDto> {
    return { accepted: true, reason: null };
  }
  async postInject(): Promise<InjectAcceptedDto> {
    return { accepted: true, reason: null, runId: "run-fixture", stageId: null, recordedUtc: new Date().toISOString() };
  }
  subscribeEvents(_onEvent: (evt: ConductorEventDto) => void, onConnectedChange: (c: boolean) => void): () => void {
    onConnectedChange(true);
    return () => {};
  }
  subscribeTranscript(onLine: (line: TranscriptLineDto) => void, onConnectedChange: (c: boolean) => void): () => void {
    onConnectedChange(true);
    for (const line of FIXTURE_TRANSCRIPT) onLine(line);
    return () => {};
  }
  dispose(): void {}
}

/** A DataSource whose every method rejects — used to prove the connection layer degrades to a
 * "disconnected" indicator instead of throwing (F6.5: "TUI crash leaves run alive"). */
export class FailingDataSource implements DataSource {
  readonly label = "failing-fixture";
  async getState(): Promise<StateDto> {
    throw new Error("engine unreachable");
  }
  async getTasks(): Promise<TasksDto> {
    throw new Error("engine unreachable");
  }
  async getProcesses(): Promise<ProcessesDto> {
    throw new Error("engine unreachable");
  }
  async getSessions(): Promise<SessionsDto> {
    throw new Error("engine unreachable");
  }
  async query(): Promise<QueryResultDto> {
    throw new Error("engine unreachable");
  }
  async postControl(): Promise<ControlAcceptedDto> {
    throw new Error("engine unreachable");
  }
  async postInject(): Promise<InjectAcceptedDto> {
    throw new Error("engine unreachable");
  }
  subscribeEvents(_onEvent: (evt: ConductorEventDto) => void, onConnectedChange: (c: boolean) => void): () => void {
    onConnectedChange(false);
    return () => {};
  }
  subscribeTranscript(_onLine: (line: TranscriptLineDto) => void, onConnectedChange: (c: boolean) => void): () => void {
    onConnectedChange(false);
    return () => {};
  }
  dispose(): void {}
}
