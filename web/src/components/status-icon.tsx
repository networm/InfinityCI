import type { JobRunStatus, RunStatus } from "@/lib/types";
import { statusColor } from "@/lib/format";

/** GitHub-Actions-style status glyph. */
export function StatusIcon({ status, size = 16 }: { status: RunStatus | JobRunStatus; size?: number }) {
  const color = statusColor(status);
  if (status === "Success") {
    return (
      <svg width={size} height={size} viewBox="0 0 16 16" fill={color.fg} aria-label="success">
        <path d="M13.78 4.22a.75.75 0 0 1 0 1.06l-7.25 7.25a.75.75 0 0 1-1.06 0L2.22 9.28a.751.751 0 0 1 .018-1.042.751.751 0 0 1 1.042-.018L6 10.94l6.72-6.72a.75.75 0 0 1 1.06 0Z" />
      </svg>
    );
  }
  if (status === "Failed") {
    return (
      <svg width={size} height={size} viewBox="0 0 16 16" fill={color.fg} aria-label="failed">
        <path d="M4.47.22A.75.75 0 0 1 5 0h6a.75.75 0 0 1 .53.22l4.25 4.25c.141.14.22.331.22.53v6a.75.75 0 0 1-.22.53l-4.25 4.25a.75.75 0 0 1-.53.22H5a.75.75 0 0 1-.53-.22L.22 11.53A.75.75 0 0 1 0 11V5a.75.75 0 0 1 .22-.53Zm5.28 4.47a.75.75 0 1 0-1.5 0v3a.75.75 0 0 0 1.5 0ZM8 11a1 1 0 1 0 0 2 1 1 0 0 0 0-2Z" />
      </svg>
    );
  }
  if (status === "Running") {
    return (
      <svg width={size} height={size} viewBox="0 0 16 16" fill={color.fg} aria-label="running" className="animate-spin">
        <path d="M8 0a8 8 0 1 1 0 16A8 8 0 0 1 8 0ZM1.5 8a6.5 6.5 0 1 0 13 0 6.5 6.5 0 0 0-13 0Zm7-3.25v2.5h2.5a.75.75 0 0 1 0 1.5h-4.25V4.75a.75.75 0 0 1 1.5 0Z" />
      </svg>
    );
  }
  if (status === "Cancelled") {
    return (
      <svg width={size} height={size} viewBox="0 0 16 16" fill={color.fg} aria-label="cancelled">
        <path d="M11.28 6.78a.75.75 0 0 0-1.06-1.06L7.25 8.69 5.78 7.22a.75.75 0 0 0-1.06 1.06l2 2a.75.75 0 0 0 1.06 0l3.5-3.5Z" />
        <path fillRule="evenodd" d="M16 8A8 8 0 1 1 0 8a8 8 0 0 1 16 0Zm-1.5 0a6.5 6.5 0 1 1-13 0 6.5 6.5 0 0 1 13 0Z" />
      </svg>
    );
  }
  // Queued / Pending / Skipped: gray ring
  return (
    <svg width={size} height={size} viewBox="0 0 16 16" fill={color.fg} aria-label={String(status).toLowerCase()}>
      <path d="M8 1.5a6.5 6.5 0 1 0 0 13 6.5 6.5 0 0 0 0-13ZM0 8a8 8 0 1 1 16 0A8 8 0 0 1 0 8Z" opacity="0.55" />
    </svg>
  );
}
