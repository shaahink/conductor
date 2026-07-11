import type {
  ConductorEventDto,
  ProcessDto,
  QueryResultDto,
  SessionRowDto,
  StateDto,
  TaskDto,
  TranscriptLineDto,
} from "../api/types.js";

export interface ToastItem {
  id: number;
  text: string;
  kind: "success" | "error" | "warn" | "info";
  createdAt: number;
}

export type PaneId = "plan" | "agent" | "process";

export type ModalKind =
  | "none"
  | "palette"
  | "inject"
  | "promptEditor"
  | "sessionHistory"
  | "report"
  | "help";

export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface UiState {
  focusedPane: PaneId;
  modal: ModalKind;
  planSelected: number; // flat index over (stage rows + visible checkpoint rows)
  processSelected: number;
  agentAutoScroll: boolean;
  agentOffset: number; // lines scrolled up from the live tail when agentAutoScroll is false
  agentSearch: string | null;
  agentSearchActive: boolean;
  agentSearchMatchIdx: number;
  agentFoldTools: boolean;
  /** Stage ids explicitly toggled by the user — overrides the "current stage auto-expanded"
   * default computed at render time. See src/components/planRows.ts. */
  expandedOverrides: Record<string, boolean>;
  planScrollTop: number;
  /** Hit regions for the current frame, filled by Layout after each render so the mouse handler
   * can map a click's (col, row) back to "which pane" without re-deriving layout math. */
  regions: Partial<Record<PaneId, Rect>>;
}

export interface ConnectionState {
  mode: "live" | "demo";
  url: string;
  eventsConnected: boolean;
  transcriptConnected: boolean;
  lastError: string | null;
}

export interface AppState {
  connection: ConnectionState;
  planState: StateDto | null;
  tasks: TaskDto[];
  processes: ProcessDto[];
  sessions: SessionRowDto[];
  events: ConductorEventDto[];
  transcript: TranscriptLineDto[];
  toasts: ToastItem[];
  ui: UiState;
  reportResult: QueryResultDto | null;
  reportLoading: boolean;
}

type Listener = () => void;

const MAX_EVENTS = 400;
const MAX_TRANSCRIPT = 4000;
let toastSeq = 0;

export class Store {
  private state: AppState;
  private listeners = new Set<Listener>();

  constructor(mode: "live" | "demo", url: string) {
    this.state = {
      connection: { mode, url, eventsConnected: false, transcriptConnected: false, lastError: null },
      planState: null,
      tasks: [],
      processes: [],
      sessions: [],
      events: [],
      transcript: [],
      toasts: [],
      reportResult: null,
      reportLoading: false,
      ui: {
        focusedPane: "plan",
        modal: "none",
        planSelected: 0,
        processSelected: 0,
        agentAutoScroll: true,
        agentOffset: 0,
        agentSearch: null,
        agentSearchActive: false,
        agentSearchMatchIdx: -1,
        agentFoldTools: true,
        expandedOverrides: {},
        planScrollTop: 0,
        regions: {},
      },
    };
  }

  getState = (): AppState => this.state;

  subscribe = (listener: Listener): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  private emit() {
    for (const l of this.listeners) l();
  }

  private set(patch: Partial<AppState>) {
    this.state = { ...this.state, ...patch };
    this.emit();
  }

  setUi(patch: Partial<UiState>) {
    this.set({ ui: { ...this.state.ui, ...patch } });
  }

  setConnection(patch: Partial<ConnectionState>) {
    this.set({ connection: { ...this.state.connection, ...patch } });
  }

  setPlanState(s: StateDto) {
    this.set({ planState: s });
  }

  setTasks(tasks: TaskDto[]) {
    this.set({ tasks });
  }

  setProcesses(processes: ProcessDto[]) {
    this.set({ processes });
  }

  setSessions(sessions: SessionRowDto[]) {
    this.set({ sessions });
  }

  setReport(loading: boolean, result: QueryResultDto | null) {
    this.set({ reportLoading: loading, reportResult: result });
  }

  pushEvent(evt: ConductorEventDto) {
    const events = [...this.state.events, evt];
    if (events.length > MAX_EVENTS) events.splice(0, events.length - MAX_EVENTS);
    this.set({ events });
  }

  pushTranscript(line: TranscriptLineDto) {
    const transcript = [...this.state.transcript, line];
    if (transcript.length > MAX_TRANSCRIPT) transcript.splice(0, transcript.length - MAX_TRANSCRIPT);
    this.set({ transcript });
  }

  toast(text: string, kind: ToastItem["kind"] = "info") {
    const item: ToastItem = { id: ++toastSeq, text, kind, createdAt: Date.now() };
    const toasts = [...this.state.toasts, item];
    this.set({ toasts });
    setTimeout(() => this.dismissToast(item.id), 3200);
  }

  dismissToast(id: number) {
    this.set({ toasts: this.state.toasts.filter((t) => t.id !== id) });
  }

  toggleStageExpanded(stageId: string, defaultExpanded: boolean) {
    const current = this.state.ui.expandedOverrides[stageId] ?? defaultExpanded;
    this.setUi({ expandedOverrides: { ...this.state.ui.expandedOverrides, [stageId]: !current } });
  }

  setRegion(pane: PaneId, rect: Rect) {
    // Avoid a render loop: only update+emit when the rect actually changed.
    const prev = this.state.ui.regions[pane];
    if (prev && prev.x === rect.x && prev.y === rect.y && prev.width === rect.width && prev.height === rect.height) return;
    this.setUi({ regions: { ...this.state.ui.regions, [pane]: rect } });
  }
}
