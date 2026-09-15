import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useNavigate, useParams } from "@tanstack/react-router";
import type { HubConnection } from "@microsoft/signalr";
import { ChevronLeft, ChevronRight, Copy, History, Pencil, Play, RefreshCw, Trash2 } from "lucide-react";

import { HistoryDrawer } from "@/components/history-drawer";
import { useTriggerWithParams } from "@/components/trigger-dialog";
import { StatusIcon } from "@/components/status-icon";
import { api, UnauthorizedError } from "@/lib/api";
import { useMe } from "@/lib/me-context";
import { useResolveUserName } from "@/lib/user-names";
import { formatDateTime, formatDuration } from "@/lib/format";
import { getCiHub } from "@/lib/signalr";
import { useTranslation } from "react-i18next";
import type { JobRun, Run, RunsPageItem, WorkflowInfo } from "@/lib/types";

const PAGE_SIZE = 20;

export function WorkflowDetailPage() {
  const { name } = useParams({ from: "/jobs/$name" });
  const navigate = useNavigate();
  const resolveName = useResolveUserName();
  const { t } = useTranslation();
  const [workflow, setWorkflow] = useState<WorkflowInfo | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  const [items, setItems] = useState<RunsPageItem[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(0); // 0-based
  const [loading, setLoading] = useState(true);
  // Live insertions only apply to page 1; refs avoid resubscribing and keep
  // the SignalR handlers free of setState-in-updater side effects.
  const pageRef = useRef(0);
  const itemsRef = useRef<RunsPageItem[]>([]);
  useEffect(() => {
    pageRef.current = page;
  }, [page]);
  useEffect(() => {
    itemsRef.current = items;
  }, [items]);
  const [refreshing, setRefreshing] = useState(false);
  const [historyOpen, setHistoryOpen] = useState(false);
  const [paramDefs, setParamDefs] = useState<import("@/lib/types").WorkflowParam[]>([]);
  const [enabled, setEnabled] = useState(true);
  const { requestTrigger, dialog } = useTriggerWithParams();

  const loadPage = useCallback(
    async (targetPage: number) => {
      setRefreshing(true);
      try {
        const result = await api.workflowRuns(name, targetPage * PAGE_SIZE, PAGE_SIZE);
        setItems(result.items);
        setTotal(result.total);
        setPage(targetPage);
      } catch (e) {
        if (e instanceof UnauthorizedError) throw e;
        setMessage(e instanceof Error ? e.message : String(e));
      } finally {
        setRefreshing(false);
        setLoading(false);
      }
    },
    [name],
  );

  useEffect(() => {
    api
      .jobs()
      .then((list) => {
        const found = list.find((w) => w.name === name);
        if (!found) setNotFound(true);
        else {
          setWorkflow(found);
          setParamDefs(found.params ?? []);
          setEnabled(found.enabled);
        }
      })
      .catch((e) => setMessage(e instanceof Error ? e.message : String(e)));
  }, [name]);

  useEffect(() => {
    void loadPage(0);
  }, [loadPage]);

  // Live updates for the runs currently visible on this page.
  useEffect(() => {
    let cancelled = false;
    let connection: HubConnection | null = null;

    (async () => {
      try {
        connection = await getCiHub();
        if (cancelled) return;

        // Join the dashboard group — runUpdated is broadcast there and to run groups.
        const initial = await connection.invoke<RunsPageItem[]>("SubscribeDashboard", 0, 30);
        connection.on("runUpdated", (run: Run) => {
          if (run.workflowName !== name) return;
          const exists = itemsRef.current.some((item) => item.run.id === run.id);
          if (exists) {
            setItems((prev) => prev.map((item) => (item.run.id === run.id ? { ...item, run } : item)));
          } else if (pageRef.current === 0) {
            // New run on the first page: prepend (GitHub style), keep page size.
            setItems((prev) => [{ run, jobs: [] }, ...prev].slice(0, PAGE_SIZE));
            setTotal((t) => t + 1);
          }
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
        if (!cancelled && initial.length > 0) {
          // If the REST page load is older than the snapshot, prefer freshest data.
          setItems((prev) => {
            const byId = new Map(prev.map((i) => [i.run.id, i]));
            // The dashboard snapshot spans all workflows — keep only this one's.
            for (const fresh of initial) {
              if (fresh.run.workflowName !== name) continue;
              const current = byId.get(fresh.run.id);
              if (!current || fresh.run.version > current.run.version) byId.set(fresh.run.id, fresh);
            }
            return [...byId.values()].sort((a, b) => b.run.id - a.run.id).slice(0, PAGE_SIZE);
          });
        }
      } catch {
        // REST paging/manual refresh still works
      }
    })();

    return () => {
      cancelled = true;
      connection?.off("runUpdated");
      connection?.off("jobUpdated");
      connection?.invoke("UnsubscribeDashboard").catch(() => {});
    };
  }, [name]);

  if (notFound) {
    return (
      <div className="rounded-md border border-line bg-canvas p-6 text-sm text-fg-muted">
        {t("workflow.notFound", { name })}
        <Link to="/jobs" className="ml-1 text-link hover:underline">{t("workflow.backToList")}</Link>
      </div>
    );
  }

  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const { me } = useMe();
  const isAdmin = me?.role === "Admin" || me?.role === "SuperAdmin";

  return (
    <div className="space-y-5">
      <Header
        name={name}
        workflow={workflow}
        enabled={enabled}
        onTrigger={() => {
          requestTrigger(name, paramDefs, (run) => {
            window.location.assign(`/runs/${run.id}`);
          });
        }}
        onHistory={() => setHistoryOpen(true)}
        onDelete={async () => {
          if (!window.confirm(t("workflow.confirmDelete", { name }))) return;
          try {
            await api.deleteJob(name);
            window.location.assign("/jobs");
          } catch (e) {
            setMessage(e instanceof Error ? e.message : String(e));
          }
        }}
      />

      {message && (
        <div className="rounded-md border border-danger-line bg-danger-subtle px-3 py-2 text-sm text-danger">{message}</div>
      )}

      <section>
        <div className="mb-2 flex items-center justify-between">
          <h2 className="text-sm font-medium text-fg-muted">{t("workflow.historyTitle", { total })}</h2>
          <button
            type="button"
            onClick={() => void loadPage(page)}
            className="flex items-center gap-1.5 rounded-md border border-line bg-canvas px-2.5 py-1 text-xs hover:bg-hover"
          >
            <RefreshCw size={12} className={refreshing ? "animate-spin" : ""} />
            {t("common.refresh")}
          </button>
        </div>

        {loading ? (
          <div className="rounded-md border border-line bg-canvas p-6 text-sm text-fg-muted">{t("common.loading")}</div>
        ) : items.length === 0 ? (
          <div className="rounded-md border border-line bg-canvas p-6 text-sm text-fg-muted">
            {t("workflow.emptyRuns")}
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
                        <span className="font-medium">#{item.run.runNumber}</span>
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

        {/* Pagination */}
        {total > 0 && (
          <div className="mt-3 flex items-center justify-between text-sm">
            <span className="text-xs text-fg-muted">
              {t("workflow.paginationRange", { from: page * PAGE_SIZE + 1, to: Math.min((page + 1) * PAGE_SIZE, total), total })}
            </span>
            <div className="flex items-center gap-2">
              <button
                type="button"
                disabled={page === 0 || refreshing}
                onClick={() => void loadPage(page - 1)}
                className="flex items-center gap-1 rounded-md border border-line bg-canvas px-2.5 py-1.5 text-xs hover:bg-hover disabled:opacity-40"
              >
                <ChevronLeft size={12} />
                {t("workflow.prevPage")}
              </button>
              <span className="text-xs text-fg-muted">
                {t("workflow.pageInfo", { current: page + 1, totalPages })}
              </span>
              <button
                type="button"
                disabled={page + 1 >= totalPages || refreshing}
                onClick={() => void loadPage(page + 1)}
                className="flex items-center gap-1 rounded-md border border-line bg-canvas px-2.5 py-1.5 text-xs hover:bg-hover disabled:opacity-40"
              >
                {t("workflow.nextPage")}
                <ChevronRight size={12} />
              </button>
            </div>
          </div>
        )}
      </section>

      {historyOpen && <HistoryDrawer name={name} isAdmin={isAdmin} onClose={() => setHistoryOpen(false)} />}
      {dialog}
    </div>
  );
}

function Header({
  name,
  workflow,
  enabled,
  onTrigger,
  onHistory,
  onDelete,
}: {
  name: string;
  workflow: WorkflowInfo | null;
  enabled: boolean;
  onTrigger: () => void;
  onHistory: () => void;
  onDelete: () => void;
}) {
  const { t } = useTranslation();
  return (
    <div className="flex items-start justify-between rounded-md border border-line bg-canvas p-4">
      <div>
        <div className="flex items-center gap-3">
          <h1 className="text-xl font-semibold">{name}</h1>
          {workflow && <span className="rounded bg-chip px-2 py-0.5 text-xs text-fg-muted">{workflow.project}</span>}
        </div>
        {workflow && (
          <div className="mt-2 flex flex-wrap gap-1.5">
            {workflow.jobs.map((job) => (
              <span key={job.key} className="rounded bg-canvas-subtle px-2 py-0.5 text-xs text-fg-muted ring-1 ring-line">
                <span className="font-medium text-fg">{job.key}</span> · {t("workflow.stepsCount", { count: job.steps })} · {job.runsOn}
                {job.needs.length > 0 && ` ← ${job.needs.join(", ")}`}
              </span>
            ))}
          </div>
        )}
      </div>
      <div className="flex items-center gap-2">
        <button
          type="button"
          disabled={false}
          onClick={onTrigger}
          className="flex items-center gap-1.5 rounded-md bg-success-btn px-3 py-1.5 text-sm font-medium text-white hover:bg-success-btn-hover disabled:opacity-50"
        >
          <Play size={13} />
          {t("common.run")}
        </button>
        <button
          type="button"
          onClick={onHistory}
          className="flex items-center gap-1.5 rounded-md border border-line px-3 py-1.5 text-sm hover:bg-hover"
        >
          <History size={13} />
          {t("workflow.configHistory")}
        </button>
        {!enabled && <span className="rounded bg-dim px-2 py-0.5 text-xs text-white">{t("common.disabled")}</span>}
        <Link
          to="/jobs/$name/edit"
          params={{ name }}
          className="flex items-center gap-1.5 rounded-md border border-line px-3 py-1.5 text-sm hover:bg-hover"
        >
          <Pencil size={13} />
          {t("common.edit")}
        </Link>
        <Link
          to="/jobs/new"
          search={{ from: name }}
          className="flex items-center gap-1.5 rounded-md border border-line px-3 py-1.5 text-sm hover:bg-hover"
        >
          <Copy size={13} />
          {t("common.copy")}
        </Link>
        <button
          type="button"
          onClick={onDelete}
          className="flex items-center gap-1.5 rounded-md border border-line px-3 py-1.5 text-sm text-danger hover:bg-danger-subtle"
        >
          <Trash2 size={13} />
          {t("common.delete")}
        </button>
      </div>
    </div>
  );
}
