import { ApiClient } from "./client.js";
import { connectSse } from "./sse.js";
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
} from "./types.js";

/** Everything the store needs — implemented by both the real HTTP client (LiveDataSource) and the
 * offline demo generator (DemoDataSource), so the rest of the app never branches on connection mode. */
export interface DataSource {
  readonly label: string;
  getState(signal?: AbortSignal): Promise<StateDto>;
  getTasks(signal?: AbortSignal): Promise<TasksDto>;
  getProcesses(signal?: AbortSignal): Promise<ProcessesDto>;
  getSessions(signal?: AbortSignal): Promise<SessionsDto>;
  query(sql: string, signal?: AbortSignal): Promise<QueryResultDto>;
  postControl(body: { command: string; stageId?: string; force?: boolean; confirmed?: boolean; value?: string }): Promise<ControlAcceptedDto>;
  postInject(content: string, stageId?: string): Promise<InjectAcceptedDto>;
  subscribeEvents(onEvent: (evt: ConductorEventDto) => void, onConnectedChange: (c: boolean) => void): () => void;
  subscribeTranscript(onLine: (line: TranscriptLineDto) => void, onConnectedChange: (c: boolean) => void): () => void;
  dispose(): void;
}

export class LiveDataSource implements DataSource {
  readonly label: string;
  private readonly api: ApiClient;
  private lastEventSeq = 0;
  private lastTranscriptSeq = 0;

  constructor(baseUrl: string) {
    this.api = new ApiClient(baseUrl);
    this.label = baseUrl;
  }

  getState(signal?: AbortSignal) {
    return this.api.getState(signal);
  }
  getTasks(signal?: AbortSignal) {
    return this.api.getTasks(signal);
  }
  getProcesses(signal?: AbortSignal) {
    return this.api.getProcesses(signal);
  }
  getSessions(signal?: AbortSignal) {
    return this.api.getSessions(signal);
  }
  query(sql: string, signal?: AbortSignal) {
    return this.api.query(sql, signal);
  }
  postControl(body: { command: string; stageId?: string; force?: boolean; confirmed?: boolean; value?: string }) {
    return this.api.postControl(body);
  }
  postInject(content: string, stageId?: string) {
    return this.api.postInject(content, stageId);
  }

  subscribeEvents(onEvent: (evt: ConductorEventDto) => void, onConnectedChange: (c: boolean) => void): () => void {
    const handle = connectSse(this.api.eventsUrl(), {
      onConnectedChange,
      since: () => this.lastEventSeq || undefined,
      onMessage: (data) => {
        try {
          const evt = JSON.parse(data) as ConductorEventDto;
          if (typeof evt.seq === "number") this.lastEventSeq = Math.max(this.lastEventSeq, evt.seq);
          onEvent(evt);
        } catch {
          /* ignore a torn/partial frame — the next one will be well-formed */
        }
      },
    });
    return () => handle.stop();
  }

  subscribeTranscript(onLine: (line: TranscriptLineDto) => void, onConnectedChange: (c: boolean) => void): () => void {
    const handle = connectSse(this.api.transcriptUrl(), {
      onConnectedChange,
      since: () => this.lastTranscriptSeq || undefined,
      onMessage: (data) => {
        try {
          const line = JSON.parse(data) as TranscriptLineDto;
          if (typeof line.seq === "number") this.lastTranscriptSeq = Math.max(this.lastTranscriptSeq, line.seq);
          onLine(line);
        } catch {
          /* ignore */
        }
      },
    });
    return () => handle.stop();
  }

  dispose(): void {
    // connectSse handles are returned per-subscription and stopped by the unsubscribe closures above.
  }
}
