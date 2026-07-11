// Mirrors src/Conductor/Core/Http/ControlPlaneDto.cs (camelCase on the wire — System.Text.Json
// CamelCase naming policy). Keep field names in lockstep with the C# records; this file has no
// codegen behind it, so a DTO change on the engine side must be echoed here by hand.

export interface CheckpointDto {
  id: string;
  title: string;
  status: string;
}

export interface StageDto {
  id: string;
  title: string;
  done: number;
  total: number;
  state: string; // confirmed | done | gating | active | skipped | todo
  attempts: number;
  lastOutcome: string;
  costUsd: number;
  parentId: string | null;
  depth: number;
  checkpoints: CheckpointDto[];
}

export interface GateDto {
  name: string;
  state: string; // pending | running | pass | fail | warn | skip
  elapsedSec: number;
}

export interface StateDto {
  planName: string;
  status: string;
  attentionReason: string | null;
  stageId: string;
  stageTitle: string;
  persona: string | null;
  doneCount: number;
  totalCount: number;
  totalCostUsd: number;
  overheadCostUsd: number;
  tokensInput: number;
  tokensOutput: number;
  tokensReasoning: number;
  currentCheckpoint: string;
  currentCheckpointTitle: string;
  gateSummary: string;
  stages: StageDto[];
  runId: string;
  repo: string;
  planDir: string;
  sessionNumber: number;
  sessionKind: string;
  attempt: number;
  maxAttempts: number;
  sessionElapsedSec: number;
  agentActive: boolean;
  sessionCostUsd: number;
  sessionTokensInput: number;
  sessionTokensOutput: number;
  sessionTokensReasoning: number;
  gates: GateDto[];
}

export interface TaskDto {
  taskId: string;
  checkpointId: string;
  title: string;
  status: string; // todo | in_progress | done | skipped
  source: string; // planner | agent | human
  order: number;
}

export interface TasksDto {
  tasks: TaskDto[];
}

export interface ProcessDto {
  pid: number;
  purpose: string;
  stageId: string | null;
  sessionNumber: number | null;
  startedUtc: string;
  exitedUtc: string | null;
  exitCode: number | null;
  alive: boolean;
  lastOutputLine: string | null;
}

export interface ProcessesDto {
  processes: ProcessDto[];
}

export interface SessionRowDto {
  number: number;
  stageId: string;
  kind: string;
  startedUtc: string;
  endedUtc: string | null;
  outcome: string | null;
  attempt: number;
  resumeCount: number;
  gateSummary: string | null;
  resultSummary: string | null;
  commitCount: number;
}

export interface SessionsDto {
  sessions: SessionRowDto[];
}

export interface QueryRowDto {
  values: string[];
}

export interface QueryResultDto {
  columns: string[];
  rows: QueryRowDto[];
  truncated: boolean;
  error: string | null;
}

export interface InjectAcceptedDto {
  accepted: boolean;
  reason: string | null;
  runId: string | null;
  stageId: string | null;
  recordedUtc: string | null;
}

export interface ControlAcceptedDto {
  accepted: boolean;
  reason: string | null;
}

/// Mirrors Core/Events/ConductorEvent.cs's polymorphic "type" discriminator. Only the fields the
/// TUI actually reads are declared; the rest ride along in the index signature.
export interface ConductorEventDto {
  type: string;
  seq: number;
  ts: string;
  runId: string;
  sessionId: string | null;
  [key: string]: unknown;
}

/// Mirrors Core/Events/TranscriptLog.cs's TranscriptLine record.
export interface TranscriptLineDto {
  seq: number;
  ts: string;
  sessionId: string | null;
  kind: string; // system | text | thinking | tool | result | stderr | raw
  text: string;
}

// The 11 control verbs ControlFile.Parse recognises (Core/ControlFile.cs).
export const CONTROL_VERBS = [
  "pause",
  "resume",
  "approve",
  "abort",
  "skip",
  "kill",
  "stop-after",
  "retry-stage",
  "rollback",
  "pause-after-stage",
  "goto",
] as const;

export type ControlVerb = (typeof CONTROL_VERBS)[number];

export const DESTRUCTIVE_VERBS: ReadonlySet<ControlVerb> = new Set([
  "abort",
  "kill",
  "rollback",
  "retry-stage",
]);

export const VERB_DESCRIPTIONS: Record<ControlVerb, string> = {
  pause: "Pause after the current session ends",
  resume: "Resume a paused/needs-human/awaiting-owner run",
  approve: "Approve an owner-gated stage (alias of resume)",
  abort: "Abort the run now (destructive)",
  skip: "Skip the current stage",
  kill: "Kill the current agent session (destructive)",
  "stop-after": "Stop the whole run when the current session ends",
  "retry-stage": "Reset the attempt counter and retry the current stage (destructive)",
  rollback: "git reset --hard to the current stage's start commit (destructive)",
  "pause-after-stage": "Pause once the current stage completes",
  goto: "Jump to a different stage (requires a target stage id)",
};
