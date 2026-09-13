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

export interface WorkflowRuntimeState {
  workflowName: string;
  enabled: boolean;
  webhookToken: string | null;
  notifyWebhookUrl: string | null;
}

export interface WorkflowParam {
  name: string;
  default: string;
  required: boolean;
  description: string | null;
}

export interface Run {
  id: number;
  workflowName: string;
  project: string;
  triggeredBy: string;
  params: Record<string, string>;
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
  needs: string[];
  steps: number;
}

export interface WorkflowInfo {
  name: string;
  project: string;
  enabled: boolean;
  jobs: WorkflowJobInfo[];
  scm?: { url: string; branch: string | null; ref: string | null; credentials: string | null } | null;
  params?: WorkflowParam[];
}

export interface Me {
  username: string;
  displayName: string;
  role: UserRole;
}

export interface DashboardItem {
  name: string;
  project: string;
  isFavorite: boolean;
  enabled: boolean;
  branch: string | null;
  commitSha: string | null;
  commitMessage: string | null;
  commitAuthor: string | null;
  commitWhen: string | null;
  lastRun: Run | null;
}

export interface CredentialInfo {
  id: number;
  name: string;
  username: string;
  createdUtc: string;
}

export interface EditorScm {
  url: string;
  branch: string;
  ref: string;
  credentials: string;
}

export interface ProjectInfo {
  id: number;
  name: string;
  description: string | null;
}

/** A job run waiting in the dispatch queue (local executor or agent pull). */
export interface QueueItemInfo {
  jobRunId: number;
  runId: number;
  workflowName: string;
  project: string;
  jobKey: string;
  runsOn: string;
  requiredLabel: string | null;
  createdAt: string;
}

export interface UserInfo {
  id: number;
  username: string;
  displayName: string | null;
  role: UserRole;
  /** False for LDAP-provisioned users that have no local password. */
  hasPassword?: boolean;
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
  environment: Record<string, string>;
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
  scm: EditorScm | null;
  jobs: EditorJob[];
}
