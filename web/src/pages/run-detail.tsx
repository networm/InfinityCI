import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useParams } from "@tanstack/react-router";
import type { HubConnection } from "@microsoft/signalr";
import { Ban, ChevronDown, ChevronRight, Download } from "lucide-react";

import { StatusIcon } from "@/components/status-icon";
import { api } from "@/lib/api";
import { layoutDag } from "@/lib/dag";
import { formatDuration, formatLogTimestamp } from "@/lib/format";
import { useResolveUserName } from "@/lib/user-names";
import { getCiHub } from "@/lib/signalr";
import type { JobRun, LogLine, Run, RunStatus, RunSubscription } from "@/lib/types";

type LogsByJob = Record<string, LogLine[]>;

export function BuildDetailPage() {
  const { runId: runIdParam } = useParams({ from: "/runs/$runId" });
  const runId = Number(runIdParam);
  const resolveName = useResolveUserName();

  const [run, setRun] = useState<Run | null>(null);
  const [jobs, setJobs] = useState<JobRun[]>([]);
  const [logs, setLogs] = useState<LogsByJob>({});
  const [selectedJob, setSelectedJob] = useState<string | null>(null);
  const [jobFilter, setJobFilter] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [cancelling, setCancelling] = useState(false);

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

  useEffect(() => {
    let cancelled = false;
    let connection: HubConnection | null = null;

    const resubscribe = async () => {
      // Reconnects replay the full snapshot; cursors dedupe overlapping lines.
      const snapshot = await connection!.invoke<RunSubscription>("SubscribeRun", runId);
      if (cancelled) return;
      applyRun(snapshot.run);
      for (const jobRun of snapshot.jobRuns) applyJob(jobRun);
      for (const [jobKey, lines] of Object.entries(snapshot.logs)) {
        for (const line of lines) applyLine(jobKey, line);
      }
      setSelectedJob((current) => current ?? snapshot.jobRuns[0]?.jobKey ?? null);
    };

    (async () => {
      try {
        connection = await getCiHub();
        if (cancelled) return;

        connection.on("logAppended", (rid: number, jobKey: string, line: LogLine) => {
          if (rid === runId) applyLine(jobKey, line);
        });
        connection.on("jobUpdated", (jobRun: JobRun) => {
          if (jobRun.runId === runId) applyJob(jobRun);
        });
        connection.on("runUpdated", (r: Run) => {
          if (r.id === runId) applyRun(r);
        });
        connection.on("reconnected", () => {
          void resubscribe().catch(() => {});
        });

        await resubscribe();
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e));
      }
    })();

    return () => {
      cancelled = true;
      connection?.off("logAppended");
      connection?.off("jobUpdated");
      connection?.off("runUpdated");
      connection?.invoke("UnsubscribeRun", runId).catch(() => {});
    };
  }, [applyJob, applyLine, applyRun, runId]);

  const filteredJobs = useMemo(
    () => jobs.filter((j) => j.jobKey.toLowerCase().includes(jobFilter.toLowerCase())),
    [jobs, jobFilter],
  );
  const currentJob = jobs.find((j) => j.jobKey === selectedJob) ?? filteredJobs[0] ?? null;
  const isRunOpen = run?.status === "Running" || run?.status === "Queued";

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          {run && <StatusIcon status={run.status} size={22} />}
          <h1 className="text-xl font-semibold">
            {run?.workflowName ?? "…"} <span className="text-[#57606a]">#{runId}</span>
          </h1>
        </div>
        <div className="flex items-center gap-3 text-xs text-[#57606a]">
          {run && (
            <span>
              由 {resolveName(run.triggeredBy)} 触发 · 项目 {run.project}
            </span>
          )}
          {isRunOpen && (
            <button
              type="button"
              disabled={cancelling}
              onClick={async () => {
                setCancelling(true);
                try {
                  await api.cancelRun(runId);
                } finally {
                  setCancelling(false);
                }
              }}
              className="flex items-center gap-1.5 rounded-md border border-[#d0d7de] bg-white px-2.5 py-1.5 text-xs text-[#cf222e] hover:bg-[#ffebe9]"
            >
              <Ban size={12} />
              取消 Run
            </button>
          )}
        </div>
      </div>

      {run && Object.keys(run.params ?? {}).length > 0 && (
        <div className="flex flex-wrap items-center gap-2 text-xs">
          <span className="text-[#57606a]">参数：</span>
          {Object.entries(run.params).map(([key, value]) => (
            <span key={key} className="rounded bg-[#eaeef2] px-1.5 py-0.5 font-mono">
              {key}=<span className="text-[#57606a]">{value}</span>
            </span>
          ))}
        </div>
      )}

      {error && (
        <div className="rounded-md border border-[#ffc1bc] bg-[#ffebe9] px-3 py-2 text-sm text-[#cf222e]">{error}</div>
      )}

      {/* Job dependency DAG (GitHub style) — rendered when the workflow uses needs */}
      <DagView
        jobs={jobs}
        runStatus={run?.status ?? null}
        selected={currentJob?.jobKey ?? null}
        onSelect={(jobKey) => setSelectedJob(jobKey)}
      />

      <div className="flex gap-4">
        {/* Parallel jobs sidebar */}
        <aside className="w-60 shrink-0">
          <input
            value={jobFilter}
            onChange={(e) => setJobFilter(e.target.value)}
            placeholder="筛选 Job"
            className="mb-2 w-full rounded-md border border-[#d0d7de] px-2.5 py-1.5 text-sm outline-none focus:border-[#0969da]"
          />
          <div className="overflow-hidden rounded-md border border-[#d0d7de] bg-white">
            {filteredJobs.map((job) => (
              <button
                key={job.id}
                type="button"
                onClick={() => setSelectedJob(job.jobKey)}
                className={
                  "flex w-full items-center gap-2 border-b border-[#d8dee4] px-3 py-2 text-left text-sm last:border-0 hover:bg-[#f6f8fa] " +
                  (currentJob?.jobKey === job.jobKey ? "bg-[#ddf4ff] font-medium" : "")
                }
              >
                <StatusIcon status={job.status} size={14} />
                <span className="truncate">{job.jobKey}</span>
                <span className="ml-auto text-xs text-[#57606a]">
                  {job.runsOn.startsWith("agent") ? "Agent" : "本地"}
                </span>
              </button>
            ))}
            {filteredJobs.length === 0 && <div className="px-3 py-3 text-xs text-[#57606a]">无匹配 Job</div>}
          </div>
        </aside>

        {/* Selected job: per-step consoles */}
        <section className="min-w-0 flex-1">
          {currentJob ? (
            <JobConsole job={currentJob} lines={logs[currentJob.jobKey] ?? []} />
          ) : (
            <div className="rounded-md border border-[#d0d7de] bg-white p-6 text-sm text-[#57606a]">选择左侧 Job 查看日志。</div>
          )}
        </section>
      </div>
    </div>
  );
}

const DAG_STATUS_STROKE: Record<string, string> = {
  Success: "#1a7f37",
  Failed: "#cf222e",
  Running: "#9a6700",
  Cancelled: "#57606a",
  Queued: "#8c959f",
  Pending: "#8c959f",
  Skipped: "#d1d9e0",
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
  const layout = useMemo(() => layoutDag(jobs, runStatus), [jobs, runStatus]);
  if (!layout) return null;

  return (
    <div className="overflow-x-auto rounded-md border border-[#d0d7de] bg-white p-4">
      <svg width={layout.width} height={layout.height + 24} role="img" aria-label="Job 依赖图" style={{ minWidth: layout.width }}>
        {layout.edges.map((edge) => {
          const highlight = selected === edge.to || selected === edge.from;
          return (
            <polyline
              key={`${edge.from}->${edge.to}`}
              points={edge.points}
              fill="none"
              stroke={highlight ? "#0969da" : "#d1d9e0"}
              strokeWidth={highlight ? 2 : 1.5}
            />
          );
        })}
        {layout.nodes.map((node) => {
          if (node.kind === "job") {
            const isSelected = node.jobKey === selected;
            const status = node.status ?? "Queued";
            const stroke = DAG_STATUS_STROKE[status] ?? "#8c959f";
            return (
              <g
                key={node.jobKey!}
                transform={`translate(${node.cx}, ${node.cy})`}
                onClick={() => onSelect(node.jobKey!)}
                style={{ cursor: "pointer" }}
              >
                <circle
                  r={14}
                  fill={isSelected ? "#ddf4ff" : "#ffffff"}
                  stroke={isSelected ? "#0969da" : stroke}
                  strokeWidth={isSelected ? 2.5 : 2}
                />
                <StatusGlyph status={status} />
                <text y={32} textAnchor="middle" fontSize={12} fill={isSelected ? "#0969da" : "#24292f"} fontFamily="inherit">
                  {node.jobKey}
                </text>
              </g>
            );
          }
          // start / end virtual nodes; the end bubble shows the run result glyph
          const isEnd = node.kind === "end";
          const status = isEnd ? node.status : null;
          const stroke = isEnd ? DAG_STATUS_STROKE[status ?? "Queued"] ?? "#8c959f" : "#57606a";
          return (
            <g key={node.kind} transform={`translate(${node.cx}, ${node.cy})`}>
              <circle r={11} fill="#f6f8fa" stroke={stroke} strokeWidth={2} />
              {isEnd ? <StatusGlyph status={status ?? "Queued"} compact /> : (
                <path d="M -4 0 L 4 0 M 0 -4 L 0 4" stroke={stroke} strokeWidth={1.5} />
              )}
              <text y={28} textAnchor="middle" fontSize={11} fill="#57606a" fontFamily="inherit">
                {isEnd ? "结束" : "开始"}
              </text>
            </g>
          );
        })}
      </svg>
    </div>
  );
}

function JobConsole({ job, lines }: { job: JobRun; lines: LogLine[] }) {
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
}: {
  index: number;
  step: import("@/lib/types").JobStep;
  lines: LogLine[];
  collapsed: boolean;
  onToggle: () => void;
}) {
  const consoleRef = useRef<HTMLDivElement | null>(null);
  const stickToBottom = useRef(true);

  return (
    <div className="overflow-hidden rounded-md border border-[#d0d7de] bg-white">
      <button
        type="button"
        onClick={onToggle}
        className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm hover:bg-[#f6f8fa]"
      >
        {collapsed ? <ChevronRight size={14} /> : <ChevronDown size={14} />}
        <StatusIcon status={step.status} size={14} />
        <span className="font-medium">{step.name}</span>
        {step.exitCode !== null && step.exitCode !== 0 && (
          <span className="text-xs text-[#cf222e]">exit {step.exitCode}</span>
        )}
        <span className="ml-auto text-xs text-[#57606a]">{formatDuration(step.startedAt, step.finishedAt)}</span>
      </button>
      {!collapsed && (
        <div
          ref={consoleRef}
          onScroll={() => {
            const el = consoleRef.current;
            if (!el) return;
            stickToBottom.current = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
          }}
          className="max-h-96 overflow-auto bg-[#0d1117] px-3 py-2 font-mono text-xs leading-5"
        >
          {lines.length === 0 ? (
            <div className="text-[#7d8590]">（暂无输出 — 等待步骤开始或输出到达）</div>
          ) : (
            lines.map((line) => (
              <div key={line.line} className="flex gap-3 whitespace-pre-wrap">
                <span className="shrink-0 select-none text-[#7d8590]">{formatLogTimestamp(line.timestampUtc)}</span>
                <span className="text-[#c9d1d9]">{line.text}</span>
              </div>
            ))
          )}
          <AutoScroll dep={lines.length} containerRef={consoleRef} stickRef={stickToBottom} />
        </div>
      )}
    </div>
  );
}

/** Keeps the console pinned to the bottom while new lines arrive, unless the user scrolled up. */
function AutoScroll({
  dep,
  containerRef,
  stickRef,
}: {
  dep: number;
  containerRef: React.RefObject<HTMLDivElement | null>;
  stickRef: React.MutableRefObject<boolean>;
}) {
  useEffect(() => {
    const el = containerRef.current;
    if (el && stickRef.current) {
      el.scrollTop = el.scrollHeight;
    }
  }, [dep, containerRef, stickRef]);
  return null;
}


/** Jenkins-style status glyph drawn inside a DAG bubble: ✓ success, ✗ failed,
///  amber arc for running, dot for queued/pending/skipped. */
function StatusGlyph({ status, compact = false }: { status: string; compact?: boolean }) {
  const scale = compact ? 0.75 : 1;
  const color = DAG_STATUS_STROKE[status] ?? "#8c959f";
  const w = 2.5 * scale;
  switch (status) {
    case "Success":
      return (
        <path
          d={`M ${-6 * scale} 0 L ${-1.5 * scale} ${4.5 * scale} L ${6 * scale} ${-4.5 * scale}`}
          fill="none"
          stroke={color}
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
          stroke={status === "Cancelled" ? "#8c959f" : color}
          strokeWidth={w}
          strokeLinecap="round"
        />
      );
    case "Running":
      return (
        <path
          className="animate-spin"
          style={{ transformBox: "fill-box", transformOrigin: "center" }}
          d="M 0 -8 A 8 8 0 1 1 -7.4 3.5"
          fill="none"
          stroke={color}
          strokeWidth={w}
          strokeLinecap="round"
        />
      );
    default:
      // Queued / Pending / Skipped
      return <circle r={3.5 * scale} fill={color} />;
  }
}

function JobHeader({ job }: { job: JobRun }) {
  const [open, setOpen] = useState(false);
  const base = `/api/runs/${job.runId}/logs/${encodeURIComponent(job.jobKey)}/download`;

  return (
    <div className="relative flex items-center gap-2 rounded-md border border-[#d0d7de] bg-white px-3 py-2.5">
      <StatusIcon status={job.status} size={18} />
      <span className="text-sm font-semibold">{job.jobKey}</span>
      {job.agentId && <span className="rounded bg-[#ddf4ff] px-1.5 py-0.5 text-xs text-[#0969da]">agent</span>}
      <span className="ml-auto text-xs text-[#57606a]">{formatDuration(job.startedAt, job.finishedAt)}</span>
      <div className="relative">
        <button
          type="button"
          onClick={() => setOpen((prev) => !prev)}
          className="flex items-center gap-1.5 rounded-md border border-[#d0d7de] px-2 py-1 text-xs hover:bg-[#f3f4f6]"
        >
          <Download size={12} />
          下载日志
        </button>
        {open && (
          <div className="absolute right-0 z-10 mt-1 w-44 overflow-hidden rounded-md border border-[#d0d7de] bg-white shadow-lg">
            <a
              href={`${base}?format=raw`}
              className="block px-3 py-2 text-xs hover:bg-[#f6f8fa]"
              onClick={() => setOpen(false)}
            >
              原始日志
            </a>
            <a
              href={`${base}?format=timestamped`}
              className="block px-3 py-2 text-xs hover:bg-[#f6f8fa]"
              onClick={() => setOpen(false)}
            >
              带时间戳日志
            </a>
          </div>
        )}
      </div>
    </div>
  );
}
