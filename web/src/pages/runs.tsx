import { useCallback, useEffect, useState } from "react";
import { Link } from "@tanstack/react-router";
import { Play, RefreshCw } from "lucide-react";
import type { HubConnection } from "@microsoft/signalr";

import { StatusIcon } from "@/components/status-icon";
import { api } from "@/lib/api";
import { useMe } from "@/lib/me-context";
import { formatDuration, formatDateTime } from "@/lib/format";
import { getCiHub } from "@/lib/signalr";
import type { JobRun, Run, RunsPageItem } from "@/lib/types";

function useRunsFeed() {
  const [items, setItems] = useState<RunsPageItem[]>([]);
  const [jobs, setJobs] = useState<{ name: string; project: string }[]>([]);
  const [jobsLoading, setJobsLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);

  const refresh = useCallback(async () => {
    setRefreshing(true);
    try {
      setItems(await api.runs());
    } catch {
      // keep current list
    } finally {
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    let cancelled = false;
    let connection: HubConnection | null = null;

    void refresh();

    (async () => {
      try {
        connection = await getCiHub();
        if (cancelled) return;

        connection.on("runUpdated", (run: Run) => {
          setItems((prev) => prev.map((item) => (item.run.id === run.id ? { ...item, run } : item)));
        });
        connection.on("jobUpdated", (jobRun: JobRun) => {
          setItems((prev) =>
            prev.map((item) =>
              item.run.id === jobRun.runId
                ? {
                    ...item,
                    jobs: item.jobs.some((j) => j.id === jobRun.id)
                      ? item.jobs.map((j) => (j.id === jobRun.id ? jobRun : j))
                      : [...item.jobs, jobRun],
                  }
                : item,
            ),
          );
        });

        const initial = await connection.invoke<RunsPageItem[]>("SubscribeDashboard");
        if (!cancelled && initial.length > 0) setItems(initial);
        connection.on("reconnected", async () => {
          const fresh = await connection!.invoke<RunsPageItem[]>("SubscribeDashboard");
          if (!cancelled) setItems(fresh);
        });
      } catch {
        // REST refresh already provides data
      }
    })();

    return () => {
      cancelled = true;
      connection?.off("runUpdated");
      connection?.off("jobUpdated");
      connection?.invoke("UnsubscribeDashboard").catch(() => {});
    };
  }, [refresh]);

  useEffect(() => {
    api
      .jobs()
      .then((list) => setJobs(list.map((j) => ({ name: j.name, project: j.project }))))
      .catch(() => {})
      .finally(() => setJobsLoading(false));
  }, []);

  return { items, jobs, jobsLoading, refreshing, refresh };
}

export function DashboardPage() {
  const { items, jobs, jobsLoading, refreshing, refresh } = useRunsFeed();
  const { me } = useMe();
  const isAdmin = me?.role === "Admin" || me?.role === "SuperAdmin";
  const [triggering, setTriggering] = useState<string | null>(null);

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-semibold">Runs</h1>
        <button
          type="button"
          onClick={() => void refresh()}
          className="flex items-center gap-1.5 rounded-md border border-[#d0d7de] bg-white px-3 py-1.5 text-sm hover:bg-[#f3f4f6]"
        >
          <RefreshCw size={14} className={refreshing ? "animate-spin" : ""} />
          刷新
        </button>
      </div>

      <section>
        <h2 className="mb-2 text-sm font-medium text-[#57606a]">任务（Run 触发）</h2>
        <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
          {jobsLoading && <div className="text-sm text-[#57606a]">加载中…</div>}
          {!jobsLoading && jobs.length === 0 && (
            <div className="rounded-md border border-[#d0d7de] bg-white p-4 text-sm text-[#57606a]">
              还没有任务。
              {isAdmin && (
                <Link to="/jobs/new" className="ml-1 text-[#0969da] hover:underline">
                  创建第一个任务 →
                </Link>
              )}
            </div>
          )}
          {jobs.map((job) => (
            <div key={job.name} className="flex items-center justify-between rounded-md border border-[#d0d7de] bg-white px-3 py-2.5">
              <div>
                <div className="text-sm font-medium">{job.name}</div>
                <div className="text-xs text-[#57606a]">{job.project}</div>
              </div>
              <button
                type="button"
                disabled={triggering === job.name}
                onClick={async () => {
                  setTriggering(job.name);
                  try {
                    const run = await api.trigger(job.name);
                    window.location.assign(`/runs/${run.id}`);
                  } catch {
                    // ignore; list stays
                  } finally {
                    setTriggering(null);
                  }
                }}
                className="flex items-center gap-1.5 rounded-md bg-[#2da44e] px-2.5 py-1.5 text-xs font-medium text-white hover:bg-[#2c974b] disabled:opacity-50"
              >
                <Play size={12} />
                Run
              </button>
            </div>
          ))}
        </div>
      </section>

      <section>
        <h2 className="mb-2 text-sm font-medium text-[#57606a]">最近的 Runs</h2>
        {items.length === 0 ? (
          <div className="rounded-md border border-[#d0d7de] bg-white p-6 text-sm text-[#57606a]">
            还没有运行记录 — 点击上方任务的 Run 按钮。
          </div>
        ) : (
          <div className="overflow-hidden rounded-md border border-[#d0d7de] bg-white">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-[#d0d7de] bg-[#f6f8fa] text-left text-xs text-[#57606a]">
                  <th className="w-10 px-3 py-2">状态</th>
                  <th className="px-3 py-2">Run</th>
                  <th className="px-3 py-2">Jobs</th>
                  <th className="px-3 py-2">触发人</th>
                  <th className="px-3 py-2">时间</th>
                  <th className="px-3 py-2">耗时</th>
                </tr>
              </thead>
              <tbody>
                {items.map((item) => (
                  <tr key={item.run.id} className="border-b border-[#d8dee4] last:border-0 hover:bg-[#f6f8fa]">
                    <td className="px-3 py-2">
                      <StatusIcon status={item.run.status} />
                    </td>
                    <td className="px-3 py-2">
                      <Link to="/runs/$runId" params={{ runId: String(item.run.id) }} className="hover:text-[#0969da] hover:underline">
                        <span className="font-medium">{item.run.workflowName}</span>{" "}
                        <span className="text-[#57606a]">#{item.run.id}</span>
                      </Link>
                    </td>
                    <td className="px-3 py-2">
                      <div className="flex items-center gap-1.5">
                        {item.jobs.map((j) => (
                          <span key={j.id} className="flex items-center gap-1 text-xs text-[#57606a]">
                            <StatusIcon status={j.status} size={12} />
                            {j.jobKey}
                          </span>
                        ))}
                      </div>
                    </td>
                    <td className="px-3 py-2 text-[#57606a]">{item.run.triggeredBy || "—"}</td>
                    <td className="px-3 py-2 text-[#57606a]">{formatDateTime(item.run.createdAt)}</td>
                    <td className="px-3 py-2 text-[#57606a]">{formatDuration(item.run.startedAt, item.run.finishedAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}
