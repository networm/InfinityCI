import { useCallback, useEffect, useState } from "react";
import { Link } from "@tanstack/react-router";
import { RefreshCw } from "lucide-react";
import { useTranslation } from "react-i18next";

import { api } from "@/lib/api";
import { formatDuration } from "@/lib/format";
import type { AgentInfo, QueueItemInfo } from "@/lib/types";

const POLL_MS = 3000;

export function QueuePage() {
  const { t } = useTranslation();
  const [items, setItems] = useState<QueueItemInfo[]>([]);
  const [agents, setAgents] = useState<AgentInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [tick, setTick] = useState(0); // re-renders wait durations between polls

  const refresh = useCallback(async () => {
    setRefreshing(true);
    try {
      const [queue, agentList] = await Promise.all([api.queue(), api.agents()]);
      setItems(queue);
      setAgents(agentList);
    } catch {
      // keep the previous list
    } finally {
      setRefreshing(false);
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void refresh();
    const poll = setInterval(() => void refresh(), POLL_MS);
    const durationTick = setInterval(() => setTick((v) => v + 1), 1000);
    return () => {
      clearInterval(poll);
      clearInterval(durationTick);
    };
  }, [refresh]);

  const onlineLabels = new Set(agents.filter((a) => a.online).flatMap((a) => a.labels));
  const anyAgentOnline = agents.some((a) => a.online);

  const waitReason = (item: QueueItemInfo) => {
    if (item.runsOn === "local") return t("queue.reason.local");
    if (item.runsOn.startsWith("agent:")) {
      const label = item.runsOn.slice("agent:".length).trim();
      return onlineLabels.has(label)
        ? t("queue.reason.agentLabelBusy", { label })
        : t("queue.reason.agentLabelOffline", { label });
    }
    return anyAgentOnline ? t("queue.reason.agentBusy") : t("queue.reason.agentOffline");
  };

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold">{t("queue.title")}</h1>
          <p className="mt-0.5 text-xs text-fg-muted">{t("queue.hint")}</p>
        </div>
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
        <div className="rounded-md border border-line bg-canvas p-6 text-sm text-fg-muted">{t("queue.empty")}</div>
      ) : (
        <div className="overflow-hidden rounded-md border border-line bg-canvas">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-line bg-canvas-subtle text-left text-xs text-fg-muted">
                <th className="w-10 px-3 py-2">{t("columns.status")}</th>
                <th className="px-3 py-2">Run</th>
                <th className="px-3 py-2">{t("editor.jobKey")}</th>
                <th className="px-3 py-2">{t("queue.target")}</th>
                <th className="px-3 py-2">{t("queue.reasonTitle")}</th>
                <th className="px-3 py-2">{t("queue.waited")}</th>
              </tr>
            </thead>
            <tbody>
              {items.map((item, index) => (
                <tr key={item.jobRunId} className="border-b border-line-muted last:border-0">
                  <td className="px-3 py-2 text-xs font-medium text-fg-muted">#{index + 1}</td>
                  <td className="px-3 py-2">
                    <Link
                      to="/runs/$runId"
                      params={{ runId: String(item.runId) }}
                      className="hover:text-link hover:underline"
                    >
                      <span className="font-medium">{item.workflowName}</span>{" "}
                      <span className="text-fg-muted">#{item.runId}</span>
                    </Link>
                  </td>
                  <td className="px-3 py-2 text-fg-muted">{item.jobKey}</td>
                  <td className="px-3 py-2">
                    <span className="rounded bg-chip px-1.5 py-0.5 font-mono text-xs">{item.runsOn}</span>
                  </td>
                  <td className="px-3 py-2 text-fg-muted">{waitReason(item)}</td>
                  <td className="px-3 py-2 text-fg-muted">{formatDuration(item.createdAt, null)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
