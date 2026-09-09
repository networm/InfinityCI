import type { AgentInfo, Build, JobDefinition, LogPage } from "./types";

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, {
    headers: { "Content-Type": "application/json" },
    ...init,
  });
  if (!res.ok) {
    throw new Error(`${init?.method ?? "GET"} ${url} failed: ${res.status} ${res.statusText}`);
  }
  return (await res.json()) as T;
}

export const api = {
  agents: () => request<AgentInfo[]>("/api/agents"),

  jobs: () => request<JobDefinition[]>("/api/jobs"),

  job: (name: string) => request<JobDefinition>(`/api/jobs/${encodeURIComponent(name)}`),

  trigger: (jobName: string) =>
    request<Build>(`/api/jobs/${encodeURIComponent(jobName)}/trigger`, { method: "POST" }),

  builds: (opts?: { job?: string; skip?: number; take?: number }) => {
    const q = new URLSearchParams();
    if (opts?.job) q.set("job", opts.job);
    if (opts?.skip) q.set("skip", String(opts.skip));
    if (opts?.take) q.set("take", String(opts.take));
    const qs = q.size > 0 ? `?${q}` : "";
    return request<Build[]>(`/api/builds${qs}`);
  },

  build: (id: number) => request<Build>(`/api/builds/${id}`),

  cancel: (id: number) =>
    request<{ cancelled: boolean }>(`/api/builds/${id}/cancel`, { method: "POST" }),

  /** Reads log lines at/after the byte cursor; NextOffset feeds the next read. */
  logs: (id: number, afterOffset: number) =>
    request<LogPage>(`/api/builds/${id}/logs?afterOffset=${afterOffset}`),
};
