import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useParams } from "@tanstack/react-router";
import { Ban, ChevronDown, ChevronLeft, ChevronRight, Download, RotateCcw } from "lucide-react";
import { useTranslation } from "react-i18next";
import { Link } from "@tanstack/react-router";

import { StatusIcon } from "@/components/status-icon";
import { XtermConsole } from "@/components/xterm-console";
import { api } from "@/lib/api";
import { layoutDag } from "@/lib/dag";
import { formatDuration } from "@/lib/format";
import { useResolveUserName } from "@/lib/user-names";
import { getCiHub } from "@/lib/signalr";
import { useHubEvent, useHubGroup, useReconnected } from "@/lib/live";
import type { JobRun, LogLine, Run, RunStatus, RunSubscription } from "@/lib/types";

type LogsByJob = Record<string, LogLine[]>;

export function BuildDetailPage() {
  const { workflow: workflowParam, runNumber: runNumberParam } = useParams({ from: "/runs/$workflow/$runNumber" });
  const workflow = decodeURIComponent(workflowParam);
  const runNumber = Number(runNumberParam);
  const resolveName = useResolveUserName();
  const { t } = useTranslation();

  const [run, setRun] = useState<Run | null>(null);
  const [jobs, setJobs] = useState<JobRun[]>([]);
  // Internal run id (resolved from workflow + runNumber) drives SignalR identity.
  const [runId, setRunId] = useState<number | null>(null);
  const [logs, setLogs] = useState<LogsByJob>({});
  const [selectedJob, setSelectedJob] = useState<string | null>(null);
  const [jobFilter, setJobFilter] = useState("");
  const [jobsCollapsed, setJobsCollapsed] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [cancelling, setCancelling] = useState(false);
  const [retrying, setRetrying] = useState(false);

  // Per-job line cursors guard against backfill/live overlap duplicates.
  const cursors = useRef<Record<string, number>>({});
  const runVersion = useRef(-1);
  const jobVersions = useRef<Record<string, number>>({});

  const applyRun = useCallback((next: Run) => {
    if (next.version >= runVersion.current) {
      runVersion.current = next.version;
      setRun(next);
    }
  }, []);

  const applyJob = useCallback((next: JobRun) => {
    if (next.version >= (jobVersions.current[next.jobKey] ?? -1)) {
      jobVersions.current[next.jobKey] = next.version;
      setJobs((prev) => {
        const index = prev.findIndex((j) => j.id === next.id);
        if (index < 0) return [...prev, next];
        const copy = [...prev];
        copy[index] = next;
        return copy;
      });
    }
  }, []);

  const applyLine = useCallback((jobKey: string, line: LogLine) => {
    const cursor = cursors.current[jobKey] ?? 0;
    if (line.line < cursor) return; // duplicate from backfill/live overlap
    cursors.current[jobKey] = line.line + 1;
    setLogs((prev) => ({ ...prev, [jobKey]: [...(prev[jobKey] ?? []), line] }));
  }, []);

  // Initial load: REST by (workflow, runNumber) resolves the internal run id.
  useEffect(() => {
    let cancelled = false;
    api
      .run(workflow, runNumber)
      .then((page) => {
        if (cancelled) return;
        setRunId(page.run.id);
        setRun(page.run);
        setJobs(page.jobs);
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e));
      });
    return () => {
      cancelled = true;
    };
  }, [workflow, runNumber]);

  const applySubscription = useCallback((snapshot: RunSubscription) => {
    applyRun(snapshot.run);
    for (const jobRun of snapshot.jobRuns) applyJob(jobRun);
    for (const [jobKey, lines] of Object.entries(snapshot.logs)) {
      for (const line of lines) applyLine(jobKey, line);
    }
  }, [applyJob, applyLine, applyRun]);

  // Join the run's group; the subscription snapshot bootstraps the page.
  // runId is null until the REST lookup resolves, then the effect re-runs.
  useHubGroup<RunSubscription | null>(
    (connection) =>
      runId === null ? Promise.resolve(null) : connection.invoke<RunSubscription>("SubscribeRun", runId),
    (connection) => (runId === null ? Promise.resolve() : connection.invoke("UnsubscribeRun", runId)),
    [runId],
    (snapshot) => {
      if (snapshot) applySubscription(snapshot);
    },
  );
  useHubEvent("logAppended", (rid: number, jobKey: string, line: LogLine) => {
    if (rid === runId) applyLine(jobKey, line);
  });
  useHubEvent("jobUpdated", (jobRun: JobRun) => {
    if (jobRun.runId === runId) applyJob(jobRun);
  });
  useHubEvent("runUpdated", (r: Run) => {
    if (r.id === runId) applyRun(r);
  });
  useReconnected(() => {
    if (runId === null) return;
    void getCiHub()
      .then((connection) => connection.invoke<RunSubscription>("SubscribeRun", runId))
      .then((snapshot) => applySubscription(snapshot))
      .catch(() => {});
  });

  const filteredJobs = useMemo(
    () => jobs.filter((j) => j.jobKey.toLowerCase().includes(jobFilter.toLowerCase())),
    [jobs, jobFilter],
  );
  // No auto-selection: the reader picks a job in the sidebar or the DAG.
  const currentJob = selectedJob === null ? null : jobs.find((j) => j.jobKey === selectedJob) ?? null;
  const isRunOpen = run?.status === "Running" || run?.status === "Queued";

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          {run && <StatusIcon status={run.status} size={22} />}
          <h1 className="text-xl font-semibold">
            <Link
              to="/jobs/$name"
              params={{ name: run?.workflowName ?? workflow }}
              className="hover:text-link hover:underline"
            >
              {run?.workflowName ?? "…"}
            </Link>{" "}
            <span className="text-fg-muted">#{run?.runNumber ?? runNumber}</span>
          </h1>
        </div>
        <div className="flex items-center gap-3 text-xs text-fg-muted">
          {run && (
            <span>{t("runDetail.triggeredBy", { name: resolveName(run.triggeredBy), project: run.project })}</span>
          )}
          {run?.status === "Failed" && (
            <button
              type="button"
              disabled={retrying}
              onClick={async () => {
                setRetrying(true);
                if (runId === null) return;
                try {
                  await api.retryRun(runId);
                } catch (e) {
                  setError(e instanceof Error ? e.message : String(e));
                } finally {
                  setRetrying(false);
                }
              }}
              className="flex items-center gap-1.5 rounded-md border border-line bg-canvas px-2.5 py-1.5 text-xs text-success hover:bg-success-subtle"
            >
              <RotateCcw size={12} />
              {t("runDetail.retryFromFailedStep")}
            </button>
          )}
          {isRunOpen && (
            <button
              type="button"
              disabled={cancelling}
              onClick={async () => {
                setCancelling(true);
                if (runId === null) return;
                try {
                  await api.cancelRun(workflow, runNumber);
                } finally {
                  setCancelling(false);
                }
              }}
              className="flex items-center gap-1.5 rounded-md border border-line bg-canvas px-2.5 py-1.5 text-xs text-danger hover:bg-danger-subtle"
            >
              <Ban size={12} />
              {t("runDetail.cancelRun")}
            </button>
          )}
        </div>
      </div>

      {run && Object.keys(run.params ?? {}).length > 0 && (
        <div className="flex flex-wrap items-center gap-2 text-xs">
          <span className="text-fg-muted">{t("runDetail.params")}</span>
          {Object.entries(run.params).map(([key, value]) => (
            <span key={key} className="rounded bg-chip px-1.5 py-0.5 font-mono">
              {key}=<span className="text-fg-muted">{value}</span>
            </span>
          ))}
        </div>
      )}

      {error && (
        <div className="rounded-md border border-danger-line bg-danger-subtle px-3 py-2 text-sm text-danger">{error}</div>
      )}

      {/* Job dependency DAG (GitHub style) — always rendered, needs or not */}
      <DagView
        jobs={jobs}
        runStatus={run?.status ?? null}
        selected={currentJob?.jobKey ?? null}
        onSelect={(jobKey) => setSelectedJob(jobKey)}
      />

      <div className="flex gap-4">
        {/* Parallel jobs sidebar — collapsible to give the log more room */}
        <aside className={jobsCollapsed ? "flex w-10 shrink-0 flex-col items-center" : "w-60 shrink-0"}>
          {jobsCollapsed ? (
            <button
              type="button"
              onClick={() => setJobsCollapsed(false)}
              title={t("runDetail.expandJobs")}
              className="rounded-md border border-line p-1.5 text-fg-muted hover:bg-hover"
            >
              <ChevronRight size={14} />
            </button>
          ) : (
            <>
              <div className="mb-2 flex w-full items-center gap-1.5">
                <input
                  value={jobFilter}
                  onChange={(e) => setJobFilter(e.target.value)}
                  placeholder={t("runDetail.filterJobs")}
                  className="min-w-0 flex-1 rounded-md border border-line px-2.5 py-1.5 text-sm outline-none focus:border-link"
                />
                <button
                  type="button"
                  onClick={() => setJobsCollapsed(true)}
                  title={t("runDetail.collapseJobs")}
                  className="shrink-0 rounded-md border border-line p-1.5 text-fg-muted hover:bg-hover"
                >
                  <ChevronLeft size={14} />
                </button>
              </div>
              <div className="w-full overflow-hidden rounded-md border border-line bg-canvas">
                {filteredJobs.map((job) => (
                  <button
                    key={job.id}
                    type="button"
                    onClick={() => setSelectedJob(job.jobKey)}
                    className={
                      "flex w-full items-center gap-2 border-b border-line-muted px-3 py-2 text-left text-sm last:border-0 hover:bg-canvas-subtle " +
                      (currentJob?.jobKey === job.jobKey ? "bg-link-subtle font-medium" : "")
                    }
                  >
                    <StatusIcon status={job.status} size={14} />
                    <span className="truncate">{job.jobKey}</span>
                    <span className="ml-auto text-xs text-fg-muted">
                      {job.runsOn.startsWith("agent") ? t("editor.runsOnAgent") : t("runDetail.local")}
                    </span>
                  </button>
                ))}
                {filteredJobs.length === 0 && <div className="px-3 py-3 text-xs text-fg-muted">{t("runDetail.noMatchingJobs")}</div>}
              </div>
            </>
          )}
        </aside>

        {/* Selected job: per-step consoles */}
        <section className="min-w-0 flex-1">
          {currentJob ? (
            <JobConsole
              key={currentJob.jobKey}
              job={currentJob}
              workflow={workflow}
              runNumber={runNumber}
              lines={logs[currentJob.jobKey] ?? []}
            />
          ) : (
            <div className="rounded-md border border-line bg-canvas p-6 text-sm text-fg-muted">{t("runDetail.selectJobHint")}</div>
          )}
        </section>
      </div>
    </div>
  );
}

const DAG_STATUS_STROKE: Record<string, string> = {
  Success: "var(--success)",
  Failed: "var(--danger)",
  Running: "var(--attention)",
  Cancelled: "var(--fg-muted)",
  Queued: "var(--dim)",
  Pending: "var(--dim)",
  Skipped: "var(--line)",
};

function DagView({
  jobs,
  runStatus,
  selected,
  onSelect,
}: {
  jobs: JobRun[];
  runStatus: RunStatus | null;
  selected: string | null;
  onSelect: (jobKey: string) => void;
}) {
  const { t } = useTranslation();
  const layout = useMemo(() => layoutDag(jobs, runStatus), [jobs, runStatus]);

  return (
    <div className="flex justify-center overflow-x-auto rounded-md border border-line bg-canvas p-4">
      <svg width={layout.width} height={layout.height + 24} role="img" aria-label={t("runDetail.dagAria")} style={{ minWidth: layout.width }}>
        {layout.edges.map((edge) => (
            <path
              key={`${edge.from}->${edge.to}`}
              d={edge.d}
              fill="none"
              style={{ stroke: "var(--line)" }}
              strokeWidth={2}
              strokeLinecap="round"
            />
        ))}
        {layout.nodes.map((node) => {
          if (node.kind === "job") {
            const isSelected = node.jobKey === selected;
            const status = node.status ?? "Queued";
            const stroke = DAG_STATUS_STROKE[status] ?? "var(--dim)";
            return (
              <g
                key={node.jobKey!}
                transform={`translate(${node.cx}, ${node.cy})`}
                onClick={() => onSelect(node.jobKey!)}
                style={{ cursor: "pointer" }}
              >
                <circle
                  r={14}
                  style={{
                    fill: isSelected ? "var(--link-subtle)" : "var(--canvas)",
                    stroke: isSelected ? "var(--link)" : stroke,
                  }}
                  strokeWidth={isSelected ? 2.5 : 2}
                />
                <StatusGlyph status={status} />
                <text
                  y={32}
                  textAnchor="middle"
                  fontSize={12}
                  style={{ fill: isSelected ? "var(--link)" : "var(--fg)" }}
                  fontFamily="inherit"
                >
                  {node.jobKey}
                </text>
              </g>
            );
          }
          // start / end virtual nodes; the end bubble shows the run result glyph
          const isEnd = node.kind === "end";
          const status = isEnd ? node.status : null;
          const stroke = isEnd ? DAG_STATUS_STROKE[status ?? "Queued"] ?? "var(--dim)" : "var(--fg-muted)";
          return (
            <g key={node.kind} transform={`translate(${node.cx}, ${node.cy})`}>
              <circle r={11} style={{ fill: "var(--canvas-subtle)", stroke }} strokeWidth={2} />
              {isEnd ? <StatusGlyph status={status ?? "Queued"} compact /> : (
                <path d="M -4 0 L 4 0 M 0 -4 L 0 4" style={{ stroke }} strokeWidth={1.5} />
              )}
              <text y={28} textAnchor="middle" fontSize={11} style={{ fill: "var(--fg-muted)" }} fontFamily="inherit">
                {isEnd ? t("runDetail.dagEnd") : t("runDetail.dagStart")}
              </text>
            </g>
          );
        })}
      </svg>
    </div>
  );
}

function JobConsole({ job, workflow, runNumber, lines }: { job: JobRun; workflow: string; runNumber: number; lines: LogLine[] }) {
  const [collapsed, setCollapsed] = useState<Record<number, boolean>>({});

  // Group lines by their step index; each step owns one console.
  const byStep = useMemo(() => {
    const map = new Map<number, LogLine[]>();
    for (const line of lines) {
      const bucket = map.get(line.stepIndex);
      if (bucket) bucket.push(line);
      else map.set(line.stepIndex, [line]);
    }
    return map;
  }, [lines]);

  const toggleCollapse = (index: number) =>
    setCollapsed((prev) => ({ ...prev, [index]: !prev[index] }));

  return (
    <div className="space-y-3">
      <JobHeader job={job} />

      {job.steps.map((step, index) => (
        <StepSection
          key={index}
          index={index}
          step={step}
          lines={byStep.get(index) ?? []}
          collapsed={collapsed[index] ?? false}
          onToggle={() => toggleCollapse(index)}
          workflow={workflow}
          runNumber={runNumber}
          jobKey={job.jobKey}
        />
      ))}
    </div>
  );
}

function StepSection({
  index,
  step,
  lines,
  collapsed,
  onToggle,
  workflow,
  runNumber,
  jobKey,
}: {
  index: number;
  step: import("@/lib/types").JobStep;
  lines: LogLine[];
  collapsed: boolean;
  onToggle: () => void;
  workflow: string;
  runNumber: number;
  jobKey: string;
}) {
  const { t } = useTranslation();

  return (
    <div className="overflow-hidden rounded-md border border-line bg-canvas">
      <div className="flex w-full items-center gap-2 px-3 py-2 text-sm hover:bg-canvas-subtle">
        <button
          type="button"
          onClick={onToggle}
          className="flex min-w-0 flex-1 items-center gap-2 text-left"
        >
          {collapsed ? <ChevronRight size={14} /> : <ChevronDown size={14} />}
          <StatusIcon status={step.status} size={14} />
          <span className="truncate font-medium">{step.name}</span>
          {step.exitCode !== null && step.exitCode !== 0 && (
            <span className="text-xs text-danger">exit {step.exitCode}</span>
          )}
        </button>
        <span className="ml-auto shrink-0 text-xs text-fg-muted">{formatDuration(step.startedAt, step.finishedAt)}</span>
        <StepDownloadMenu workflow={workflow} runNumber={runNumber} jobKey={jobKey} stepIndex={index} />
      </div>
      {!collapsed && (
        <div className="bg-[#0d1117] p-3">
          {lines.length === 0 ? (
            <div className="text-xs text-[#7d8590]">{t("runDetail.noOutput")}</div>
          ) : (
            <XtermConsole lines={lines} live={step.status === "Running"} />
          )}
        </div>
      )}
    </div>
  );
}

/** Per-step log download: raw or timestamped, filtered by step index server-side. */
function StepDownloadMenu({
  workflow,
  runNumber,
  jobKey,
  stepIndex,
}: {
  workflow: string;
  runNumber: number;
  jobKey: string;
  stepIndex: number;
}) {
  const [open, setOpen] = useState(false);
  const { t } = useTranslation();
  const url = (format: "raw" | "timestamped") => api.runLogDownloadUrl(workflow, runNumber, jobKey, format, stepIndex);

  return (
    <div className="relative shrink-0">
      <button
        type="button"
        onClick={() => setOpen((prev) => !prev)}
        title={t("runDetail.downloadLog")}
        className="flex items-center gap-1 rounded-md border border-line px-1.5 py-1 text-xs text-fg-muted hover:bg-hover"
      >
        <Download size={12} />
      </button>
      {open && (
        <div className="absolute right-0 z-10 mt-1 w-44 overflow-hidden rounded-md border border-line bg-canvas shadow-lg">
          <a href={url("raw")} className="block px-3 py-2 text-xs hover:bg-canvas-subtle" onClick={() => setOpen(false)}>
            {t("runDetail.rawLog")}
          </a>
          <a
            href={url("timestamped")}
            className="block px-3 py-2 text-xs hover:bg-canvas-subtle"
            onClick={() => setOpen(false)}
          >
            {t("runDetail.timestampedLog")}
          </a>
        </div>
      )}
    </div>
  );
}


/** Jenkins-style status glyph drawn inside a DAG bubble: ✓ success, ✗ failed,
///  amber arc for running, dot for queued/pending/skipped. */
function StatusGlyph({ status, compact = false }: { status: string; compact?: boolean }) {
  const scale = compact ? 0.75 : 1;
  const color = DAG_STATUS_STROKE[status] ?? "var(--dim)";
  const w = 2.5 * scale;
  switch (status) {
    case "Success":
      return (
        <path
          d={`M ${-6 * scale} 0 L ${-1.5 * scale} ${4.5 * scale} L ${6 * scale} ${-4.5 * scale}`}
          fill="none"
          style={{ stroke: color }}
          strokeWidth={w}
          strokeLinecap="round"
          strokeLinejoin="round"
        />
      );
    case "Failed":
    case "Cancelled":
      return (
        <path
          d={`M ${-5 * scale} ${-5 * scale} L ${5 * scale} ${5 * scale} M ${5 * scale} ${-5 * scale} L ${-5 * scale} ${5 * scale}`}
          style={{ stroke: status === "Cancelled" ? "var(--dim)" : color }}
          strokeWidth={w}
          strokeLinecap="round"
        />
      );
    case "Running":
      return (
        <path
          className="animate-spin"
          style={{ transformBox: "fill-box", transformOrigin: "center", stroke: color }}
          d="M 0 -8 A 8 8 0 1 1 -7.4 3.5"
          fill="none"
          strokeWidth={w}
          strokeLinecap="round"
        />
      );
    default:
      // Queued / Pending / Skipped
      return <circle r={3.5 * scale} style={{ fill: color }} />;
  }
}

function JobHeader({ job }: { job: JobRun }) {
  return (
    <div className="flex items-center gap-2 rounded-md border border-line bg-canvas px-3 py-2.5">
      <StatusIcon status={job.status} size={18} />
      <span className="text-sm font-semibold">{job.jobKey}</span>
      {job.agentId && <span className="rounded bg-link-subtle px-1.5 py-0.5 text-xs text-link">agent</span>}
      <span className="ml-auto text-xs text-fg-muted">{formatDuration(job.startedAt, job.finishedAt)}</span>
    </div>
  );
}
