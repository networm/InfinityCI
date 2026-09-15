import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate } from "@tanstack/react-router";
import { RefreshCw } from "lucide-react";
import type { HubConnection } from "@microsoft/signalr";
import { useTranslation } from "react-i18next";

import { StatusIcon } from "@/components/status-icon";
import { api } from "@/lib/api";
import { formatDateTime, formatDuration } from "@/lib/format";
import { useResolveUserName } from "@/lib/user-names";
import { getCiHub } from "@/lib/signalr";
import type { JobRun, Run, RunsPageItem } from "@/lib/types";

export function RunsListPage() {
  const navigate = useNavigate();
  const resolveName = useResolveUserName();
  const { t } = useTranslation();
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
        <h1 className="text-xl font-semibold">{t("nav.runs")}</h1>
        <button
          type="button"
          onClick={() => void refresh()}
          className="flex items-center gap-1.5 rounded-md border border-line bg-canvas px-3 py-1.5 text-sm hover:bg-hover"
        >
          <RefreshCw size={14} className={refreshing ? "animate-spin" : ""} />
          {t("common.refresh")}
        </button>
      </div>

      {loading ? (
        <div className="text-sm text-fg-muted">{t("common.loading")}</div>
      ) : items.length === 0 ? (
        <div className="rounded-md border border-line bg-canvas p-6 text-sm text-fg-muted">
          {t("runs.empty")}
        </div>
      ) : (
        <div className="overflow-hidden rounded-md border border-line bg-canvas">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-line bg-canvas-subtle text-left text-xs text-fg-muted">
                <th className="w-10 px-3 py-2">{t("columns.status")}</th>
                <th className="px-3 py-2">{t("columns.run")}</th>
                <th className="px-3 py-2">{t("columns.jobs")}</th>
                <th className="px-3 py-2">{t("columns.triggeredBy")}</th>
                <th className="px-3 py-2">{t("columns.time")}</th>
                <th className="px-3 py-2">{t("columns.duration")}</th>
              </tr>
            </thead>
            <tbody>
              {items.map((item) => (
                <tr
                  key={item.run.id}
                  onClick={() =>
                    navigate({
                      to: "/runs/$workflow/$runNumber",
                      params: { workflow: item.run.workflowName, runNumber: String(item.run.runNumber) },
                    })
                  }
                  className="cursor-pointer border-b border-line-muted last:border-0 hover:bg-canvas-subtle"
                >
                  <td className="px-3 py-2">
                    <StatusIcon status={item.run.status} />
                  </td>
                  <td className="px-3 py-2">
                    <Link
                      to="/runs/$workflow/$runNumber"
                      params={{ workflow: item.run.workflowName, runNumber: String(item.run.runNumber) }}
                      className="hover:text-link hover:underline"
                    >
                      <span className="font-medium">{item.run.workflowName}</span>{" "}
                      <span className="text-fg-muted">#{item.run.runNumber}</span>
                    </Link>
                  </td>
                  <td className="px-3 py-2">
                    <div className="flex items-center gap-1.5">
                      {item.jobs.map((j) => (
                        <span key={j.id} className="flex items-center gap-1 text-xs text-fg-muted">
                          <StatusIcon status={j.status} size={12} />
                          {j.jobKey}
                        </span>
                      ))}
                    </div>
                  </td>
                  <td className="px-3 py-2 text-fg-muted">{resolveName(item.run.triggeredBy)}</td>
                  <td className="px-3 py-2 text-fg-muted">{formatDateTime(item.run.createdAt)}</td>
                  <td className="px-3 py-2 text-fg-muted">{formatDuration(item.run.startedAt, item.run.finishedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
