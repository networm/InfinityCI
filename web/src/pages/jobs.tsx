import { useEffect, useState } from "react";
import { Link } from "@tanstack/react-router";
import { Copy, History, Pencil, Play, Plus, Trash2 } from "lucide-react";
import { useTranslation } from "react-i18next";

import { HistoryDrawer } from "@/components/history-drawer";
import { api } from "@/lib/api";
import { useMe } from "@/lib/me-context";
import type { WorkflowInfo } from "@/lib/types";

export function JobsPage() {
  const { me } = useMe();
  const { t } = useTranslation();
  const isAdmin = me?.role === "Admin" || me?.role === "SuperAdmin";
  const [workflows, setWorkflows] = useState<WorkflowInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [message, setMessage] = useState<string | null>(null);
  const [historyFor, setHistoryFor] = useState<string | null>(null);

  const reload = () =>
    api
      .jobs()
      .then(setWorkflows)
      .finally(() => setLoading(false));

  useEffect(() => {
    void reload();
  }, []);

  const remove = async (name: string) => {
    if (!window.confirm(t("jobs.confirmDelete", { name }))) return;
    try {
      await api.deleteJob(name);
      await reload();
    } catch (e) {
      setMessage(e instanceof Error ? e.message : String(e));
    }
  };

  const trigger = async (name: string) => {
    try {
      const run = await api.trigger(name);
      window.location.assign(`/runs/${run.id}`);
    } catch (e) {
      setMessage(e instanceof Error ? e.message : String(e));
    }
  };

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold">{t("nav.jobs")}</h1>
          <p className="mt-0.5 text-xs text-fg-muted">
            {t("jobs.cloneHint")}{" "}
            <code className="rounded bg-chip px-1.5 py-0.5 font-mono text-xs">
              {"git clone http://用户:密码@host:5000/git/{任务名}"}
            </code>
          </p>
        </div>
        {isAdmin && (
          <Link
            to="/jobs/new"
            className="flex items-center gap-1.5 rounded-md bg-success-btn px-3 py-1.5 text-sm font-medium text-white hover:bg-success-btn-hover"
          >
            <Plus size={14} />
            {t("jobs.new")}
          </Link>
        )}
      </div>

      {message && (
        <div className="rounded-md border border-danger-line bg-danger-subtle px-3 py-2 text-sm text-danger">{message}</div>
      )}

      {loading ? (
        <div className="text-sm text-fg-muted">{t("common.loading")}</div>
      ) : workflows.length === 0 ? (
        <div className="rounded-md border border-line bg-canvas p-6 text-sm text-fg-muted">
          {t("jobs.empty")}
          {isAdmin ? t("jobs.emptyAdminHint") : t("jobs.emptyUserHint")}
        </div>
      ) : (
        <div className="overflow-hidden rounded-md border border-line bg-canvas">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-line bg-canvas-subtle text-left text-xs text-fg-muted">
                <th className="px-4 py-2">{t("columns.name")}</th>
                <th className="px-4 py-2">{t("columns.project")}</th>
                <th className="px-4 py-2">Jobs</th>
                <th className="px-4 py-2 text-right">{t("columns.actions")}</th>
              </tr>
            </thead>
            <tbody>
              {workflows.map((workflow) => (
                <tr key={workflow.name} className="border-b border-line-muted last:border-0 hover:bg-canvas-subtle">
                  <td className="px-4 py-2.5 font-medium">
                    <Link to="/jobs/$name" params={{ name: workflow.name }} className="hover:text-link hover:underline">
                      {workflow.name}
                    </Link>
                  </td>
                  <td className="px-4 py-2.5 text-fg-muted">{workflow.project}</td>
                  <td className="px-4 py-2.5 text-fg-muted">
                    <div className="flex flex-wrap gap-1.5">
                      {workflow.jobs.map((job) => (
                        <span key={job.key} className="rounded bg-chip px-1.5 py-0.5 text-xs">
                          {job.key}
                          {job.needs.length > 0 && ` ← ${job.needs.join(",")}`}
                        </span>
                      ))}
                    </div>
                  </td>
                  <td className="px-4 py-2.5">
                    <div className="flex items-center justify-end gap-2">
                      <button
                        type="button"
                        onClick={() => void trigger(workflow.name)}
                        className="flex items-center gap-1 rounded-md bg-success-btn px-2 py-1 text-xs text-white hover:bg-success-btn-hover"
                      >
                        <Play size={12} />
                        Run
                      </button>
                      <button
                        type="button"
                        onClick={() => setHistoryFor(workflow.name)}
                        className="flex items-center gap-1 rounded-md border border-line px-2 py-1 text-xs hover:bg-hover"
                      >
                        <History size={12} />
                        {t("common.history")}
                      </button>
                      {isAdmin && (
                        <>
                          <Link
                            to="/jobs/$name/edit"
                            params={{ name: workflow.name }}
                            className="flex items-center gap-1 rounded-md border border-line px-2 py-1 text-xs hover:bg-hover"
                          >
                            <Pencil size={12} />
                            {t("common.edit")}
                          </Link>
                          <Link
                            to="/jobs/new"
                            search={{ from: workflow.name }}
                            className="flex items-center gap-1 rounded-md border border-line px-2 py-1 text-xs hover:bg-hover"
                          >
                            <Copy size={12} />
                            {t("common.copy")}
                          </Link>
                          <button
                            type="button"
                            onClick={() => void remove(workflow.name)}
                            className="flex items-center gap-1 rounded-md border border-line px-2 py-1 text-xs text-danger hover:bg-danger-subtle"
                          >
                            <Trash2 size={12} />
                            {t("common.delete")}
                          </button>
                        </>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {historyFor && <HistoryDrawer name={historyFor} isAdmin={isAdmin} onClose={() => setHistoryFor(null)} />}
    </div>
  );
}
