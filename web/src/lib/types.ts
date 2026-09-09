// Mirrors the server API payloads (System.Text.Json, camelCase, enums as strings).

export type BuildStatus = "Queued" | "Running" | "Success" | "Failed" | "Cancelled";
export type BuildStepStatus = "Pending" | "Running" | "Success" | "Failed" | "Skipped" | "Cancelled";

export interface BuildStep {
  name: string;
  status: BuildStepStatus;
  startedAt: string | null;
  finishedAt: string | null;
  exitCode: number | null;
  /** Byte offset in the build log where this step's output starts. */
  startOffset: number;
  /** Byte offset where this step's output ends (exclusive). */
  endOffset: number;
}

export interface Build {
  id: number;
  jobName: string;
  status: BuildStatus;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  exitCode: number | null;
  /** Monotonic mutation counter; drop updates whose version is older than seen. */
  version: number;
  steps: BuildStep[];
}

export interface JobStep {
  name: string;
  command: string;
  shell: string | null;
  continueOnError: boolean;
  environment: Record<string, string>;
}

export interface JobDefinition {
  name: string;
  description: string | null;
  environment: Record<string, string>;
  steps: JobStep[];
}

export interface LogLine {
  offset: number;
  text: string;
}

export interface LogPage {
  buildId: number;
  nextOffset: number;
  lines: LogLine[];
}

export interface BuildSubscription {
  build: Build;
  lines: LogLine[];
}

export interface AgentInfo {
  id: string;
  name: string;
  version: string;
  labels: string[];
  online: boolean;
  maxConcurrentBuilds: number;
  runningBuilds: number;
  cpuPercent: number;
  memoryPercent: number;
  freeDiskBytes: number;
  lastSeenUtc: string;
}
