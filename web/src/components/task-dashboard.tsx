import { useCallback, useEffect, useState } from "react";
import { Link } from "@tanstack/react-router";
import { Play, RefreshCw, Star } from "lucide-react";
import type { HubConnection } from "@microsoft/signalr";

import { StatusIcon } from "@/components/status-icon";
import { api } from "@/lib/api";
import { formatDuration } from "@/lib/format";
import { getCiHub } from "@/lib/signalr";
import type { DashboardItem, Run } from "@/lib/types";

/** Status → row background tint + left accent bar (Blue Ocean style). */
function rowColors(status: string | null): { bg: string; bar: string } {
  switch (status) {
    case "Success":
      return { bg: "#f0fff4", bar: "#1a7f37" };
    case "Failed":
      return { bg: "#fff1f0", bar: "#cf222e" };
    case "Running":
      return { bg: "#fff8db", bar: "#9a6700" };
    case "Queued":
      return { bg: "#f6f8fa", bar: "#57606a" };
    default:
      return { bg: "#ffffff", bar: "#d0d7de" };
  }
}

export function TaskDashboard() {
  const [items, setItems] = useState<DashboardItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [triggering, setTriggering] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [stale, setStale] = useState(false);

  const refresh = useCallback(async () => {
    setRefreshing(true);
    try {
      setItems(await api.dashboard());
    } catch (e) {
      setMessage(e instanceof Error ? e.message : String(e));
    } finally {
      setRefreshing(false);
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  // Debounced refresh when job-level events hint at activity below run level.
  useEffect(() => {
    if (!stale) return;
    const timer = setTimeout(() => {
      setStale(false);
      void refresh();
    }, 800);
    return () => clearTimeout(timer);
  }, [stale, refresh]);

  // Live updates: keep each row's lastRun in sync with dashboard events.
  useEffect(() => {
    let cancelled = false;
    let connection: HubConnection | null = null;

    (async () => {
      try {
        connection = await getCiHub();
        if (cancelled) return;

        connection.on("runUpdated", (run: Run) => {
          setItems((prev) => prev.map((item) => (item.name === run.workflowName ? { ...item, lastRun: run } : item)));
        });
        // Job-level updates can't change the run's own status — but they hint
        // the run is progressing; refresh its row from the server cheaply.
        connection.on("jobUpdated", () => setStale(true));
        await connection.invoke("SubscribeDashboard", 0, 30);
      } catch {
        // manual refresh still works
      }
    })();

    return () => {
      cancelled = true;
      connection?.off("runUpdated");
      connection?.off("jobUpdated");
      connection?.invoke("UnsubscribeDashboard").catch(() => {});
    };
  }, []);

  const toggleFavorite = async (name: string) => {
    try {
      const { isFavorite } = await api.toggleFavorite(name);
      setItems((prev) => prev.map((item) => (item.name === name ? { ...item, isFavorite } : item)));
    } catch (e) {
      setMessage(e instanceof Error ? e.message : String(e));
    }
  };

  const trigger = async (name: string) => {
    setTriggering(name);
    try {
      const run = await api.trigger(name);
      window.location.assign(`/runs/${run.id}`);
    } catch (e) {
      setMessage(e instanceof Error ? e.message : String(e));
    } finally {
      setTriggering(null);
    }
  };

  const favorites = items.filter((i) => i.isFavorite);
  const others = items.filter((i) => !i.isFavorite);

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-semibold">任务</h1>
        <button
          type="button"
          onClick={() => void refresh()}
          className="flex items-center gap-1.5 rounded-md border border-[#d0d7de] bg-white px-3 py-1.5 text-sm hover:bg-[#f3f4f6]"
        >
          <RefreshCw size={14} className={refreshing ? "animate-spin" : ""} />
          刷新
        </button>
      </div>

      {message && (
        <div className="rounded-md border border-[#ffc1bc] bg-[#ffebe9] px-3 py-2 text-sm text-[#cf222e]">{message}</div>
      )}

      {loading ? (
        <div className="text-sm text-[#57606a]">加载中…</div>
      ) : items.length === 0 ? (
        <div className="rounded-md border border-[#d0d7de] bg-white p-6 text-sm text-[#57606a]">
          还没有任务。
          <Link to="/jobs/new" className="ml-1 text-[#0969da] hover:underline">
            创建第一个任务 →
          </Link>
        </div>
      ) : (
        <>
          {favorites.length > 0 && (
            <section>
              <h2 className="mb-2 flex items-center gap-1.5 text-sm font-medium text-[#57606a]">
                <Star size={13} className="fill-[#eac54f] text-[#eac54f]" />
                收藏任务
              </h2>
              <div className="space-y-2">
                {favorites.map((item) => (
                  <WorkflowRow key={item.name} item={item} triggering={triggering} onTrigger={trigger} onToggleFavorite={toggleFavorite} />
                ))}
              </div>
            </section>
          )}

          <section>
            {favorites.length > 0 && <h2 className="mb-2 text-sm font-medium text-[#57606a]">全部任务</h2>}
            <div className="space-y-2">
              {others.map((item) => (
                <WorkflowRow key={item.name} item={item} triggering={triggering} onTrigger={trigger} onToggleFavorite={toggleFavorite} />
              ))}
            </div>
          </section>
        </>
      )}

    </div>
  );
}

function WorkflowRow({
  item,
  triggering,
  onTrigger,
  onToggleFavorite,
}: {
  item: DashboardItem;
  triggering: string | null;
  onTrigger: (name: string) => void;
  onToggleFavorite: (name: string) => void;
}) {
  const lastStatus = item.lastRun?.status ?? null;
  const colors = rowColors(lastStatus);

  return (
    <div
      className="flex items-center gap-3 rounded-md border border-[#d0d7de] px-3 py-2.5"
      style={{ backgroundColor: colors.bg, borderLeft: `4px solid ${colors.bar}` }}
    >
      <StatusIcon status={(lastStatus ?? "Queued") as never} size={18} />
      <button
        type="button"
        title={item.isFavorite ? "取消收藏" : "收藏"}
        onClick={() => onToggleFavorite(item.name)}
        className="shrink-0"
      >
        <Star size={16} className={item.isFavorite ? "fill-[#eac54f] text-[#eac54f]" : "text-[#8c959f] hover:text-[#eac54f]"} />
      </button>
      <Link
        to="/jobs/$name"
        params={{ name: item.name }}
        className="shrink-0 text-sm font-semibold hover:text-[#0969da] hover:underline"
      >
        {item.name}
      </Link>
      <span className="shrink-0 rounded bg-[#eaeef2] px-1.5 py-0.5 text-xs text-[#57606a]">{item.project}</span>

      {/* branch + latest commit */}
      <div className="hidden min-w-0 flex-1 items-center gap-2 md:flex">
        {item.branch && (
          <span className="shrink-0 rounded bg-[#ddf4ff] px-1.5 py-0.5 font-mono text-xs text-[#0969da]">{item.branch}</span>
        )}
        {item.commitSha && (
          <span className="truncate text-xs text-[#57606a]">
            <code className="font-mono text-[#0969da]">{item.commitSha.slice(0, 7)}</code>{" "}
            {item.commitMessage && <span className="mr-1.5">{item.commitMessage}</span>}
            {item.commitAuthor && <span>· {item.commitAuthor}</span>}
          </span>
        )}
      </div>

      {/* last build */}
      <div className="ml-auto hidden shrink-0 items-center gap-2 text-xs text-[#57606a] lg:flex">
        {item.lastRun ? (
          <>
            <Link to="/runs/$runId" params={{ runId: String(item.lastRun.id) }} className="hover:text-[#0969da] hover:underline">
              #{item.lastRun.id}
            </Link>
            <span>{lastStatus}</span>
            <span>{formatDuration(item.lastRun.startedAt, item.lastRun.finishedAt)}</span>
          </>
        ) : (
          <span>从未运行</span>
        )}
      </div>

      <button
        type="button"
        disabled={triggering === item.name}
        onClick={() => onTrigger(item.name)}
        className="ml-2 flex shrink-0 items-center gap-1 rounded-md bg-[#2da44e] px-2.5 py-1.5 text-xs font-medium text-white hover:bg-[#2c974b] disabled:opacity-50"
      >
        <Play size={12} />
        Run
      </button>
    </div>
  );
}
