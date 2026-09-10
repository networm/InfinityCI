// Mirrors the server API payloads (System.Text.Json, camelCase, enums as strings).

export type RunStatus = "Queued" | "Running" | "Success" | "Failed" | "Cancelled";
export type JobRunStatus = "Queued" | "Running" | "Success" | "Failed" | "Cancelled" | "Pending" | "Skipped";
export type UserRole = "SuperAdmin" | "Admin" | "User";

export interface JobStep {
  name: string;
  status: JobRunStatus;
  startedAt: string | null;
  finishedAt: string | null;
  exitCode: number | null;
  /** Line index in the job's log where this step's output starts. */
  startLine: number;
  /** Line index where this step's output ends (exclusive). */
  endLine: number;
}

export interface JobRun {
  id: number;
  runId: number;
  jobKey: string;
  runsOn: string;
  /** Job keys this job waits for (GitHub `needs`). */
  needs: string[];
  agentId: string | null;
  status: JobRunStatus;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  exitCode: number | null;
  version: number;
  steps: JobStep[];
}

export interface Run {
  id: number;
  workflowName: string;
  project: string;
  triggeredBy: string;
  status: RunStatus;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  version: number;
}

export interface LogLine {
  line: number;
  timestampUtc: string;
  stepIndex: number;
  text: string;
}

export interface RunSubscription {
  run: Run;
  jobRuns: JobRun[];
  logs: Record<string, LogLine[]>;
}

export interface RunsPageItem {
  run: Run;
  jobs: JobRun[];
}

export interface WorkflowJobInfo {
  key: string;
  runsOn: string;
  steps: number;
}

export interface WorkflowInfo {
  name: string;
  project: string;
  jobs: WorkflowJobInfo[];
}

export interface Me {
  username: string;
  role: UserRole;
}

export interface ProjectInfo {
  id: number;
  name: string;
  description: string | null;
}

export interface UserInfo {
  id: number;
  username: string;
  role: UserRole;
  projects: { projectId: number; name: string }[];
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

export interface EnrolledAgent {
  id: string;
  name: string;
  labels: string[];
  maxConcurrentBuilds: number;
  enabled: boolean;
  enrolledAt: string;
  lastSeenUtc: string | null;
  online: boolean;
}

export interface AgentEnrollment {
  id: number;
  token: string;
  name: string;
  labelsJson: string;
  maxConcurrentBuilds: number;
  createdUtc: string;
  usedByAgentId: string | null;
}

// ---- job editor models ----

export interface EditorStep {
  name: string;
  command: string;
  shell: string;
  continueOnError: boolean;
}

export interface EditorJob {
  key: string;
  runsOn: string; // local | agent | agent:<label>
  steps: EditorStep[];
}

export interface EditorWorkflow {
  name: string;
  project: string;
  jobs: EditorJob[];
}
