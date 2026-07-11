import type {
  ControlAcceptedDto,
  InjectAcceptedDto,
  ProcessesDto,
  QueryResultDto,
  SessionsDto,
  StateDto,
  TasksDto,
} from "./types.js";

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status?: number,
  ) {
    super(message);
  }
}

/** Thin REST wrapper over the F5/F6 HTTP control plane (Core/Http/ControlPlaneServer.cs). */
export class ApiClient {
  constructor(private readonly baseUrl: string) {}

  get url(): string {
    return this.baseUrl;
  }

  async getState(signal?: AbortSignal): Promise<StateDto> {
    return this.getJson<StateDto>("/state", signal);
  }

  async getTasks(signal?: AbortSignal): Promise<TasksDto> {
    return this.getJson<TasksDto>("/tasks", signal);
  }

  async getProcesses(signal?: AbortSignal): Promise<ProcessesDto> {
    return this.getJson<ProcessesDto>("/processes", signal);
  }

  async getSessions(signal?: AbortSignal): Promise<SessionsDto> {
    return this.getJson<SessionsDto>("/sessions", signal);
  }

  async query(sql: string, signal?: AbortSignal): Promise<QueryResultDto> {
    return this.getJson<QueryResultDto>(`/report/query?sql=${encodeURIComponent(sql)}`, signal);
  }

  async postControl(body: {
    command: string;
    stageId?: string;
    force?: boolean;
    confirmed?: boolean;
    value?: string;
  }): Promise<ControlAcceptedDto> {
    return this.postJson<ControlAcceptedDto>("/control", body);
  }

  async postInject(content: string, stageId?: string): Promise<InjectAcceptedDto> {
    return this.postJson<InjectAcceptedDto>("/inject", { content, stageId });
  }

  eventsUrl(): string {
    return `${this.baseUrl}/events`;
  }

  transcriptUrl(): string {
    return `${this.baseUrl}/transcript/current`;
  }

  private async getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
    const res = await fetch(`${this.baseUrl}${path}`, { signal });
    if (!res.ok) throw new ApiError(`GET ${path} -> ${res.status}`, res.status);
    return (await res.json()) as T;
  }

  private async postJson<T>(path: string, body: unknown): Promise<T> {
    const res = await fetch(`${this.baseUrl}${path}`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
    });
    const text = await res.text();
    let parsed: T;
    try {
      parsed = JSON.parse(text) as T;
    } catch {
      throw new ApiError(`POST ${path} -> ${res.status} (unparseable body)`, res.status);
    }
    if (!res.ok && res.status !== 400 && res.status !== 202) {
      throw new ApiError(`POST ${path} -> ${res.status}`, res.status);
    }
    return parsed;
  }
}
