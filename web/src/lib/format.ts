import type { BuildStatus, BuildStepStatus } from "./types";
import type { BadgeProps } from "@/components/ui/badge";

/** Maps a build status to the matching badge variant color. */
export function buildStatusVariant(status: BuildStatus): NonNullable<BadgeProps["variant"]> {
  switch (status) {
    case "Success":
      return "success";
    case "Failed":
      return "failed";
    case "Running":
      return "running";
    case "Cancelled":
      return "cancelled";
    default:
      return "queued";
  }
}

export function stepStatusVariant(status: BuildStepStatus): NonNullable<BadgeProps["variant"]> {
  switch (status) {
    case "Success":
      return "success";
    case "Failed":
      return "failed";
    case "Running":
      return "running";
    case "Cancelled":
      return "cancelled";
    case "Skipped":
      return "outline";
    default:
      return "queued";
  }
}

export function formatTime(iso: string | null): string {
  if (!iso) return "—";
  return new Date(iso).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

export function formatDuration(startedAt: string | null, finishedAt: string | null): string {
  if (!startedAt) return "—";
  const end = finishedAt ? Date.parse(finishedAt) : Date.now();
  const ms = Math.max(0, end - Date.parse(startedAt));
  if (ms < 1000) return `${ms}ms`;
  const s = Math.floor(ms / 1000);
  if (s < 60) return `${s}s`;
  return `${Math.floor(s / 60)}m ${s % 60}s`;
}
