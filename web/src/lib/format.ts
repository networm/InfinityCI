import i18next from "i18next";

import type { JobRunStatus, RunStatus } from "./types";

/** GitHub-Actions-style status glyph colors. */
export function statusColor(status: RunStatus | JobRunStatus): { fg: string; bg: string } {
  switch (status) {
    case "Success":
      return { fg: "#1a7f37", bg: "#dafbe1" };
    case "Failed":
      return { fg: "#cf222e", bg: "#ffebe9" };
    case "Running":
      return { fg: "#9a6700", bg: "#fff8c5" };
    case "Queued":
      return { fg: "#57606a", bg: "#eaeef2" };
    case "Cancelled":
      return { fg: "#57606a", bg: "#eaeef2" };
    case "Pending":
      return { fg: "#57606a", bg: "#eaeef2" };
    case "Skipped":
      return { fg: "#57606a", bg: "#eaeef2" };
    default:
      return { fg: "#57606a", bg: "#eaeef2" };
  }
}

/** Localized display name for a run/job/step status. */
export function statusLabel(status: RunStatus | JobRunStatus): string {
  return i18next.t(`status.${status}`);
}

export function formatDuration(startedAt: string | null, finishedAt: string | null): string {
  if (!startedAt) return "—";
  const end = finishedAt ? Date.parse(finishedAt) : Date.now();
  const totalSeconds = Math.floor(Math.max(0, end - Date.parse(startedAt)) / 1000);
  if (totalSeconds < 1) return i18next.t("duration.lessThanOne");

  const days = Math.floor(totalSeconds / 86400);
  const hours = Math.floor((totalSeconds % 86400) / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  // Show the largest non-zero unit plus the next one down ("1天3小时" /
  // "1d 3h", "3小时5分" / "3h 5m"); a lone leading unit drops zero followers.
  const units: [number, string][] = [
    [days, i18next.t("duration.day")],
    [hours, i18next.t("duration.hour")],
    [minutes, i18next.t("duration.minute")],
    [seconds, i18next.t("duration.second")],
  ];
  const lead = units.findIndex(([value]) => value > 0);
  const shown = units.slice(lead, lead + 2).filter(([value], index) => index === 0 || value > 0);
  return shown.map(([value, label]) => `${value}${label}`).join("");
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
