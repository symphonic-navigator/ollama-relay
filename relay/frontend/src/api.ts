// Management API client. The access token lives in memory only; the refresh token is an
// HttpOnly cookie the browser sends to /mgmt/auth by itself.

export type User = { id: string; username: string; isAdmin: boolean; mustChangePassword: boolean };
export type Session = { accessToken: string; user: User };

let accessToken: string | null = null;
let onSessionChange: (user: User | null) => void = () => {};

export function setSessionListener(listener: (user: User | null) => void) {
  onSessionChange = listener;
}

export function applySession(session: Session | null) {
  accessToken = session?.accessToken ?? null;
  onSessionChange(session?.user ?? null);
}

export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
  }
}

let refreshing: Promise<boolean> | null = null;

export async function refresh(): Promise<boolean> {
  refreshing ??= (async () => {
    try {
      const res = await fetch("/mgmt/auth/refresh", { method: "POST", credentials: "same-origin" });
      if (!res.ok) {
        applySession(null);
        return false;
      }
      applySession((await res.json()) as Session);
      return true;
    } finally {
      setTimeout(() => (refreshing = null), 0);
    }
  })();
  return refreshing;
}

async function request<T>(method: string, path: string, body?: unknown, retry = true): Promise<T> {
  const headers: Record<string, string> = {};
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (accessToken) headers["Authorization"] = `Bearer ${accessToken}`;
  const res = await fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    credentials: "same-origin",
  });
  if (res.status === 401 && retry && !path.startsWith("/mgmt/auth/")) {
    if (await refresh()) return request<T>(method, path, body, false);
  }
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const data = await res.json();
      if (data?.error) message = typeof data.error === "string" ? data.error : JSON.stringify(data.error);
    } catch {
      /* no body */
    }
    throw new ApiError(res.status, message);
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const api = {
  get: <T>(path: string) => request<T>("GET", path),
  post: <T>(path: string, body: unknown = {}) => request<T>("POST", path, body),
  put: <T>(path: string, body: unknown) => request<T>("PUT", path, body),
  patch: <T>(path: string, body: unknown) => request<T>("PATCH", path, body),
  del: <T>(path: string) => request<T>("DELETE", path),
};

export async function login(username: string, password: string) {
  applySession(await api.post<Session>("/mgmt/auth/login", { username, password }));
}

export async function logout() {
  try {
    await api.post("/mgmt/auth/logout");
  } finally {
    applySession(null);
  }
}

// ---- shared types

export type ApiKey = { id: string; name: string; prefix: string; createdAt: string; lastUsedAt: string | null };
export type MyModel = { alias: string; online: boolean; loaded: boolean };
export type UsageRow = {
  day: string;
  alias: string;
  user?: string;
  userId?: string;
  node?: string;
  nodeId?: string;
  requests: number;
  errors: number;
  promptTokens: number;
  completionTokens: number;
  durationMs: number;
};

export type StreamInfo = {
  id: string;
  user: string;
  alias: string;
  endpoint: string;
  node: string | null;
  since: string;
  queued: boolean;
};

export type NodeInfo = {
  id: string;
  name: string;
  maxConcurrency: number;
  hasToken: boolean;
  tokenPrefix: string | null;
  tokenIssuedAt: string | null;
  online: boolean;
  connectedAt: string | null;
  lastHeartbeatAt: string | null;
  lastSeenAt: string | null;
  agentVersion: string | null;
  ollamaVersion: string | null;
  ollamaReachable: boolean | null;
  activeLeases: number;
  host: { cpuCount: number; loadAverage1: number | null; memoryTotalBytes: number | null; memoryAvailableBytes: number | null } | null;
  loaded: { name: string; aliases: string[]; size: number; sizeVram: number; expiresAt: string | null; contextLength: number | null }[];
  streams: StreamInfo[];
  reportedModels: number;
};

export type NodesResponse = { queueDepth: number; queued: StreamInfo[]; nodes: NodeInfo[] };

export type Publication = { id: string; nodeId: string; node: string; upstreamModel: string; alias: string; createdAt: string };

export type AdminUser = {
  id: string;
  username: string;
  isAdmin: boolean;
  disabled: boolean;
  mustChangePassword: boolean;
  createdAt: string;
  lastLoginAt: string | null;
  apiKeys: number;
};
