import type {
  AgentEnrollment,
  AgentInfo,
  CredentialInfo,
  DashboardItem,
  EnrolledAgent,
  LogLine,
  QueueItemInfo,
  WorkflowRuntimeState,
  Me,
  ProjectInfo,
  Run,
  RunsPageItem,
  UserInfo,
  WorkflowInfo,
} from "./types";

export class UnauthorizedError extends Error {}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, {
    headers: { "Content-Type": "application/json" },
    ...init,
  });
  if (res.status === 401) {
    throw new UnauthorizedError(url);
  }
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = (await res.json()) as { message?: string };
      if (body.message) message = body.message;
    } catch {
      // not a JSON error body
    }
    throw new Error(message);
  }
  return (await res.json()) as T;
}

export const api = {
  me: () => request<Me>("/api/me"),
  dashboard: () => request<DashboardItem[]>("/api/dashboard"),
  toggleFavorite: (name: string) =>
    request<{ isFavorite: boolean }>(`/api/jobs/${encodeURIComponent(name)}/favorite`, { method: "POST" }),

  credentials: () => request<CredentialInfo[]>("/api/credentials"),
  saveCredential: (name: string, username: string, secret: string) =>
    request<{ name: string }>("/api/credentials", { method: "POST", body: JSON.stringify({ name, username, secret }) }),
  deleteCredential: (name: string) =>
    request<unknown>(`/api/credentials/${encodeURIComponent(name)}`, { method: "DELETE" }),
  login: (username: string, password: string) =>
    request<Me>("/api/auth/login", { method: "POST", body: JSON.stringify({ username, password }) }),
  logout: () => request<unknown>("/api/auth/logout", { method: "POST" }),
  authConfig: () => request<{ ldapEnabled: boolean }>("/api/auth/config"),

  // runs
  runs: (skip = 0, take = 30) => request<RunsPageItem[]>(`/api/runs?skip=${skip}&take=${take}`),
  queue: () => request<QueueItemInfo[]>("/api/queue"),
  workflowRuns: (name: string, skip = 0, take = 20) =>
    request<{ total: number; items: RunsPageItem[] }>(
      `/api/jobs/${encodeURIComponent(name)}/runs?skip=${skip}&take=${take}`,
    ),
  run: (id: number) => request<RunsPageItem>(`/api/runs/${id}`),
  cancelRun: (id: number) =>
    request<{ cancelled: boolean }>(`/api/runs/${id}/cancel`, { method: "POST" }),
  retryRun: (id: number) => request<Run>(`/api/runs/${id}/retry`, { method: "POST" }),
  runLogs: (id: number, jobKey: string, afterLine: number) =>
    request<{ runId: number; jobKey: string; nextLine: number; lines: LogLine[] }>(
      `/api/runs/${id}/logs/${encodeURIComponent(jobKey)}?afterLine=${afterLine}`,
    ),

  // workflows (任务)
  jobs: () => request<WorkflowInfo[]>("/api/jobs"),
  jobRaw: (name: string) => request<{ name: string; yaml: string }>(`/api/jobs/${encodeURIComponent(name)}/raw`),
  createJob: (yaml: string) => request<{ name: string }>("/api/jobs", { method: "POST", body: JSON.stringify({ yaml }) }),
  updateJob: (name: string, yaml: string) =>
    request<{ name: string }>(`/api/jobs/${encodeURIComponent(name)}`, { method: "PUT", body: JSON.stringify({ yaml }) }),
  deleteJob: (name: string) =>
    request<unknown>(`/api/jobs/${encodeURIComponent(name)}`, { method: "DELETE" }),
  jobHistory: (name: string) =>
    request<{ sha: string; message: string; author: string; when: string }[]>(
      `/api/jobs/${encodeURIComponent(name)}/history`,
    ),
  jobBlob: (name: string, sha: string) =>
    request<{ name: string; sha: string; yaml: string }>(
      `/api/jobs/${encodeURIComponent(name)}/blob/${sha}`,
    ),
  restoreJob: (name: string, sha: string) =>
    request<{ name: string; restoredFrom: string }>(`/api/jobs/${encodeURIComponent(name)}/restore`, {
      method: "POST",
      body: JSON.stringify({ sha }),
    }),
  setWorkflowEnabled: (name: string, enabled: boolean) =>
    request<{ name: string; enabled: boolean }>(`/api/jobs/${encodeURIComponent(name)}/enabled`, {
      method: "POST",
      body: JSON.stringify({ enabled }),
    }),
  workflowState: (name: string) =>
    request<WorkflowRuntimeState>(`/api/jobs/${encodeURIComponent(name)}/state`),
  issueWebhookToken: (name: string) =>
    request<{ name: string; token: string; url: string; curl: string }>(
      `/api/jobs/${encodeURIComponent(name)}/webhook-token`,
      { method: "POST" },
    ),
  revokeWebhookToken: (name: string) =>
    request<unknown>(`/api/jobs/${encodeURIComponent(name)}/webhook-token`, { method: "DELETE" }),
  setNotifyWebhook: (name: string, url: string | null) =>
    request<{ name: string; url: string | null }>(`/api/jobs/${encodeURIComponent(name)}/notify-webhook`, {
      method: "PUT",
      body: JSON.stringify({ url }),
    }),

  trigger: (name: string, params?: Record<string, string>) =>
    request<Run>(`/api/jobs/${encodeURIComponent(name)}/trigger`, {
      method: "POST",
      body: JSON.stringify({ params: params ?? {} }),
    }),
  userNames: () => request<Record<string, string>>("/api/users/names"),

  // projects
  projects: () => request<ProjectInfo[]>("/api/projects"),
  createProject: (name: string, description: string) =>
    request<ProjectInfo>("/api/projects", { method: "POST", body: JSON.stringify({ name, description }) }),
  deleteProject: (id: number) => request<unknown>(`/api/projects/${id}`, { method: "DELETE" }),

  // users
  users: () =>
    request<UserInfo[]>("/api/users"),
  createUser: (username: string, password: string, role: string, projectIds: number[], displayName?: string) =>
    request<{ id: number }>("/api/users", {
      method: "POST",
      body: JSON.stringify({ username, password, role, projectIds, displayName }),
    }),
  updateUser: (id: number, payload: { password?: string; role?: string; projectIds?: number[]; displayName?: string }) =>
    request<unknown>(`/api/users/${id}`, { method: "PUT", body: JSON.stringify(payload) }),
  deleteUser: (id: number) => request<unknown>(`/api/users/${id}`, { method: "DELETE" }),

  // agents
  agents: () => request<AgentInfo[]>("/api/agents"),
  enrolledAgents: () => request<EnrolledAgent[]>("/api/agents/enrolled"),
  setAgentEnabled: (id: string, enabled: boolean) =>
    request<unknown>(`/api/agents/${encodeURIComponent(id)}/enabled`, {
      method: "PUT",
      body: JSON.stringify({ enabled }),
    }),
  deleteAgent: (id: string) => request<unknown>(`/api/agents/${encodeURIComponent(id)}`, { method: "DELETE" }),
  enrollments: () => request<AgentEnrollment[]>("/api/agents/enrollments"),
  createEnrollment: (name: string, labels: string[], maxConcurrentBuilds: number) =>
    request<{ id: number; token: string; command: string }>("/api/agents/enrollments", {
      method: "POST",
      body: JSON.stringify({ name, labels, maxConcurrentBuilds }),
    }),
  deleteEnrollment: (id: number) =>
    request<unknown>(`/api/agents/enrollments/${id}`, { method: "DELETE" }),
};

