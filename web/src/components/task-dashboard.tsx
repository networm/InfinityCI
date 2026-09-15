import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate } from "@tanstack/react-router";
import { Play, RefreshCw, Star } from "lucide-react";
import type { HubConnection } from "@microsoft/signalr";

import { StatusIcon } from "@/components/status-icon";
import { useTriggerWithParams } from "@/components/trigger-dialog";
import { api } from "@/lib/api";
import { formatDuration, statusLabel } from "@/lib/format";
import { getCiHub } from "@/lib/signalr";
import { useTranslation } from "react-i18next";
import type { DashboardItem, Run } from "@/lib/types";

/** Status → row background tint + left accent bar (Blue Ocean style). CSS
///  variables so rows follow the active light/dark theme. */
function rowColors(status: string | null): { bg: string; bar: string } {
  switch (status) {
    case "Success":
      return { bg: "var(--success-subtle)", bar: "var(--success)" };
    case "Failed":
      return { bg: "var(--danger-subtle)", bar: "var(--danger)" };
    case "Running":
      return { bg: "var(--attention-subtle)", bar: "var(--attention)" };
    case "Queued":
      return { bg: "var(--canvas-subtle)", bar: "var(--fg-muted)" };
    default:
      return { bg: "var(--canvas)", bar: "var(--line)" };
  }
}

export function TaskDashboard() {
  const { t } = useTranslation();
  const { requestTrigger, dialog } = useTriggerWithParams();
  const navigate = useNavigate();
  const [items, setItems] = useState<DashboardItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [stale, setStale] = useState(false);

  const [paramDefs, setParamDefs] = useState<Record<string, import("@/lib/types").WorkflowParam[]>>({});

  const refresh = useCallback(async () => {
    setRefreshing(true);
    try {
      setItems(await api.dashboard());
      const jobs = await api.jobs();
      setParamDefs(Object.fromEntries(jobs.map((j) => [j.name, j.params ?? []])));
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

  const trigger = (name: string) => {
    requestTrigger(name, paramDefs[name] ?? [], (run) => {
      window.location.assign(`/runs/${encodeURIComponent(run.workflowName)}/${run.runNumber}`);
    });
  };

  const favorites = items.filter((i) => i.isFavorite);
  const others = items.filter((i) => !i.isFavorite);
  // Group the non-favorite rows by project (alphabetical) — the dashboard is
  // the project-oriented overview of all workflows.
  const projectGroups = (() => {
    const byProject = new Map<string, DashboardItem[]>();
    for (const item of others) {
      const list = byProject.get(item.project) ?? [];
      list.push(item);
      byProject.set(item.project, list);
    }
    return [...byProject.entries()].sort(([a], [b]) => a.localeCompare(b));
  })();

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-semibold">{t("dashboard.title")}</h1>
        <button
          type="button"
          onClick={() => void refresh()}
          className="flex items-center gap-1.5 rounded-md border border-line bg-canvas px-3 py-1.5 text-sm hover:bg-hover"
        >
          <RefreshCw size={14} className={refreshing ? "animate-spin" : ""} />
          {t("common.refresh")}
        </button>
      </div>

      {message && (
        <div className="rounded-md border border-danger-line bg-danger-subtle px-3 py-2 text-sm text-danger">{message}</div>
      )}

      {loading ? (
        <div className="text-sm text-fg-muted">{t("common.loading")}</div>
      ) : items.length === 0 ? (
        <div className="rounded-md border border-line bg-canvas p-6 text-sm text-fg-muted">
          {t("dashboard.empty")}
          <Link to="/jobs/new" className="ml-1 text-link hover:underline">
            {t("dashboard.createFirst")}
          </Link>
        </div>
      ) : (
        <>
          {favorites.length > 0 && (
            <section>
              <h2 className="mb-2 flex items-center gap-1.5 text-sm font-medium text-fg-muted">
                <Star size={13} className="fill-[#eac54f] text-[#eac54f]" />
                {t("dashboard.favorites")}
              </h2>
              <div className="space-y-2">
                {favorites.map((item) => (
                  <WorkflowRow key={item.name} item={item} onTrigger={trigger} onToggleFavorite={toggleFavorite}
                    onOpen={(target) => navigate({ to: target })} />
                ))}
              </div>
            </section>
          )}

          {projectGroups.map(([project, groupItems]) => (
            <section key={project}>
              <h2 className="mb-2 text-sm font-medium text-fg-muted">{project}</h2>
              <div className="space-y-2">
                {groupItems.map((item) => (
                  <WorkflowRow key={item.name} item={item} onTrigger={trigger} onToggleFavorite={toggleFavorite}
                    onOpen={(target) => navigate({ to: target })} />
                ))}
              </div>
            </section>
          ))}
        </>
      )}

      {dialog}
    </div>
  );
}

function WorkflowRow({
  item,
  onTrigger,
  onToggleFavorite,
  onOpen,
}: {
  item: DashboardItem;
  onTrigger: (name: string) => void;
  onToggleFavorite: (name: string) => void;
  onOpen: (target: string) => void;
}) {
  const { t } = useTranslation();
  const lastStatus = item.lastRun?.status ?? null;
  const colors = rowColors(lastStatus);
  // Whole card clickable: with runs → latest run; never-run → task detail.
  const openTarget = item.lastRun
    ? `/runs/${encodeURIComponent(item.name)}/${item.lastRun.runNumber}`
    : `/jobs/${item.name}`;

  return (
    <div
      onClick={() => onOpen(openTarget)}
      className="flex cursor-pointer items-center gap-3 rounded-md border border-line px-3 py-2.5 transition-shadow hover:shadow-md"
      style={{ backgroundColor: colors.bg, borderLeft: `4px solid ${colors.bar}` }}
    >
      <StatusIcon status={(lastStatus ?? "Queued") as never} size={18} />
      <button
        type="button"
        title={item.isFavorite ? t("dashboard.unfavorite") : t("dashboard.favorite")}
        onClick={(e) => {
          e.stopPropagation();
          onToggleFavorite(item.name);
        }}
        className="shrink-0"
      >
        <Star size={16} className={item.isFavorite ? "fill-[#eac54f] text-[#eac54f]" : "text-dim hover:text-[#eac54f]"} />
      </button>
      <Link
        to="/jobs/$name"
        params={{ name: item.name }}
        onClick={(e) => e.stopPropagation()}
        className="shrink-0 text-sm font-semibold hover:text-link hover:underline"
      >
        {item.name}
      </Link>
      <span className="shrink-0 rounded bg-chip px-1.5 py-0.5 text-xs text-fg-muted">{item.project}</span>
      {!item.enabled && <span className="shrink-0 rounded bg-dim px-1.5 py-0.5 text-xs text-white">{t("common.disabled")}</span>}

      {/* branch + latest commit */}
      <div className="hidden min-w-0 flex-1 items-center gap-2 md:flex">
        {item.branch && (
          <span className="shrink-0 rounded bg-link-subtle px-1.5 py-0.5 font-mono text-xs text-link">{item.branch}</span>
        )}
        {item.commitSha && (
          <span className="truncate text-xs text-fg-muted">
            <code className="font-mono text-link">{item.commitSha.slice(0, 7)}</code>{" "}
            {item.commitMessage && <span className="mr-1.5">{item.commitMessage}</span>}
            {item.commitAuthor && <span>· {item.commitAuthor}</span>}
          </span>
        )}
      </div>

      {/* last build */}
      <div className="ml-auto hidden shrink-0 items-center gap-2 text-xs text-fg-muted lg:flex">
        {item.lastRun ? (
          <>
            <Link
              to="/runs/$workflow/$runNumber"
              params={{ workflow: item.name, runNumber: String(item.lastRun.runNumber) }}
              className="hover:text-link hover:underline"
            >
              #{item.lastRun.runNumber}
            </Link>
            <span>{lastStatus && statusLabel(lastStatus)}</span>
            <span>{formatDuration(item.lastRun.startedAt, item.lastRun.finishedAt)}</span>
          </>
        ) : (
          <span>{t("dashboard.neverRun")}</span>
        )}
      </div>

      <button
        type="button"
        disabled={!item.enabled}
        title={!item.enabled ? t("dashboard.disabledTitle") : undefined}
        onClick={(e) => {
          e.stopPropagation();
          onTrigger(item.name);
        }}
        className="ml-2 flex shrink-0 items-center gap-1 rounded-md bg-success-btn px-2.5 py-1.5 text-xs font-medium text-white hover:bg-success-btn-hover disabled:cursor-not-allowed disabled:bg-dim disabled:opacity-60"
      >
        <Play size={12} />
        Run
      </button>
    </div>
  );
}
