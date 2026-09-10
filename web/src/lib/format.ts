import type { JobRunStatus, RunStatus } from "./types";

/** GitHub-Actions-style status glyph colors and labels. */
export function statusColor(status: RunStatus | JobRunStatus): { fg: string; bg: string; label: string } {
  switch (status) {
    case "Success":
      return { fg: "#1a7f37", bg: "#dafbe1", label: "成功" };
    case "Failed":
      return { fg: "#cf222e", bg: "#ffebe9", label: "失败" };
    case "Running":
      return { fg: "#9a6700", bg: "#fff8c5", label: "运行中" };
    case "Queued":
      return { fg: "#57606a", bg: "#eaeef2", label: "排队中" };
    case "Cancelled":
      return { fg: "#57606a", bg: "#eaeef2", label: "已取消" };
    case "Pending":
      return { fg: "#57606a", bg: "#eaeef2", label: "等待" };
    case "Skipped":
      return { fg: "#57606a", bg: "#eaeef2", label: "跳过" };
    default:
      return { fg: "#57606a", bg: "#eaeef2", label: status };
  }
}

export function formatDuration(startedAt: string | null, finishedAt: string | null): string {
  if (!startedAt) return "—";
  const end = finishedAt ? Date.parse(finishedAt) : Date.now();
  const ms = Math.max(0, end - Date.parse(startedAt));
  if (ms < 1000) return `${ms}ms`;
  const s = Math.floor(ms / 1000);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${s % 60}s`;
  return `${Math.floor(m / 60)}h ${m % 60}m`;
}

export function formatTime(iso: string | null): string {
  if (!iso) return "—";
  return new Date(iso).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

export function formatDateTime(iso: string | null): string {
  if (!iso) return "—";
  return new Date(iso).toLocaleString([], {
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
}

/** Renders a log line's UTC ISO timestamp as a local HH:mm:ss.SSS column value. */
export function formatLogTimestamp(timestampUtc: string): string {
  const date = new Date(timestampUtc);
  if (Number.isNaN(date.getTime())) return timestampUtc;
  const pad = (n: number, width = 2) => String(n).padStart(width, "0");
  return `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}.${pad(date.getMilliseconds(), 3)}`;
}
