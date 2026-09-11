import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate } from "@tanstack/react-router";
import { RefreshCw } from "lucide-react";
import type { HubConnection } from "@microsoft/signalr";

import { StatusIcon } from "@/components/status-icon";
import { api } from "@/lib/api";
import { formatDateTime, formatDuration } from "@/lib/format";
import { useResolveUserName } from "@/lib/user-names";
import { getCiHub } from "@/lib/signalr";
import type { JobRun, Run, RunsPageItem } from "@/lib/types";

export function RunsListPage() {
  const navigate = useNavigate();
  const resolveName = useResolveUserName();
  const [items, setItems] = useState<RunsPageItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);

  const refresh = useCallback(async () => {
    setRefreshing(true);
    try {
      setItems(await api.runs(0, 30));
    } catch {
      // keep current list
    } finally {
      setRefreshing(false);
      setLoading(false);
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

        const initial = await connection.invoke<RunsPageItem[]>("SubscribeDashboard", 0, 30);
        if (!cancelled && initial.length > 0) setItems(initial);
        connection.on("reconnected", async () => {
          const fresh = await connection!.invoke<RunsPageItem[]>("SubscribeDashboard", 0, 30);
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

      {loading ? (
        <div className="text-sm text-[#57606a]">加载中…</div>
      ) : items.length === 0 ? (
        <div className="rounded-md border border-[#d0d7de] bg-white p-6 text-sm text-[#57606a]">
          还没有运行记录 — 在 Dashboard 里点击任务的 Run 按钮。
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
                <tr
                  key={item.run.id}
                  onClick={() => navigate({ to: "/runs/$runId", params: { runId: String(item.run.id) } })}
                  className="cursor-pointer border-b border-[#d8dee4] last:border-0 hover:bg-[#f6f8fa]"
                >
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
                  <td className="px-3 py-2 text-[#57606a]">{resolveName(item.run.triggeredBy)}</td>
                  <td className="px-3 py-2 text-[#57606a]">{formatDateTime(item.run.createdAt)}</td>
                  <td className="px-3 py-2 text-[#57606a]">{formatDuration(item.run.startedAt, item.run.finishedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
