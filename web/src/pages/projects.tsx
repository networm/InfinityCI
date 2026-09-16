import { useCallback, useEffect, useState } from "react";
import { Link } from "@tanstack/react-router";
import { Plus, Trash2 } from "lucide-react";
import { useTranslation } from "react-i18next";

import { api } from "@/lib/api";
import { useMe } from "@/lib/me-context";
import { useHubEvent, useHubGroup, useReconnected } from "@/lib/live";
import type { ProjectInfo, WorkflowInfo } from "@/lib/types";

export function ProjectsPage() {
  const { t } = useTranslation();
  const { me } = useMe();
  const isAdmin = me?.role === "Admin" || me?.role === "SuperAdmin";
  const [projects, setProjects] = useState<ProjectInfo[]>([]);
  const [workflows, setWorkflows] = useState<WorkflowInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [message, setMessage] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");

  const reload = useCallback(async () => {
    try {
      const [projectList, workflowList] = await Promise.all([api.projects(), api.jobs()]);
      setProjects(projectList);
      setWorkflows(workflowList);
    } catch (e) {
      setMessage(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  // Project changes from any session refresh this page live.
  useHubGroup(
    (connection) => connection.invoke("SubscribeProjects"),
    (connection) => connection.invoke("UnsubscribeProjects"),
  );
  useHubEvent("projectsChanged", () => void reload());
  useHubEvent("workflowChanged", () => void reload());
  useReconnected(() => void reload());

  const create = async () => {
    try {
      await api.createProject(name.trim(), description);
      setName("");
      setDescription("");
      await reload();
    } catch (e) {
      setMessage(e instanceof Error ? e.message : String(e));
    }
  };

  const remove = async (project: ProjectInfo, workflowCount: number) => {
    if (workflowCount > 0) return; // server rejects it too
    if (!window.confirm(t("projects.confirmDelete", { name: project.name }))) return;
    try {
      await api.deleteProject(project.id);
      await reload();
    } catch (e) {
      setMessage(e instanceof Error ? e.message : String(e));
    }
  };

  const workflowsIn = (projectName: string) => workflows.filter((w) => w.project === projectName);

  return (
    <div className="space-y-5">
      <h1 className="text-xl font-semibold">{t("projects.title")}</h1>
      <p className="text-xs text-fg-muted">{t("projects.hint")}</p>

      {message && (
        <div className="rounded-md border border-danger-line bg-danger-subtle px-3 py-2 text-sm text-danger">{message}</div>
      )}

      {isAdmin && (
        <div className="flex flex-wrap items-end gap-3 rounded-md border border-line bg-canvas p-4">
          <label className="text-sm">
            <span className="mb-1 block text-xs font-medium text-fg-muted">{t("admin.newProjectName")}</span>
            <input
              value={name}
              onChange={(e) => setName(e.target.value)}
              className="w-56 rounded-md border border-line px-2.5 py-1.5 text-sm outline-none focus:border-link"
            />
          </label>
          <label className="text-sm">
            <span className="mb-1 block text-xs font-medium text-fg-muted">{t("projects.description")}</span>
            <input
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              className="w-72 rounded-md border border-line px-2.5 py-1.5 text-sm outline-none focus:border-link"
            />
          </label>
          <button
            type="button"
            disabled={!name.trim()}
            onClick={() => void create()}
            className="flex items-center gap-1.5 rounded-md bg-success-btn px-3 py-1.5 text-sm font-medium text-white hover:bg-success-btn-hover disabled:opacity-50"
          >
            <Plus size={14} />
            {t("admin.createProject")}
          </button>
        </div>
      )}

      {loading ? (
        <div className="text-sm text-fg-muted">{t("common.loading")}</div>
      ) : projects.length === 0 ? (
        <div className="rounded-md border border-line bg-canvas p-6 text-sm text-fg-muted">{t("admin.noProjects")}</div>
      ) : (
        <div className="overflow-hidden rounded-md border border-line bg-canvas">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-line bg-canvas-subtle text-left text-xs text-fg-muted">
                <th className="px-4 py-2">{t("columns.name")}</th>
                <th className="px-4 py-2">{t("projects.description")}</th>
                <th className="px-4 py-2">{t("nav.jobs")}</th>
                {isAdmin && <th className="px-4 py-2 text-right">{t("columns.actions")}</th>}
              </tr>
            </thead>
            <tbody>
              {projects.map((project) => {
                const projectWorkflows = workflowsIn(project.name);
                return (
                  <tr key={project.id} className="border-b border-line-muted last:border-0 align-top">
                    <td className="px-4 py-2.5 font-medium">{project.name}</td>
                    <td className="px-4 py-2.5 text-fg-muted">{project.description || "—"}</td>
                    <td className="px-4 py-2.5">
                      {projectWorkflows.length === 0 ? (
                        <span className="text-xs text-fg-muted">{t("projects.noWorkflows")}</span>
                      ) : (
                        <div className="flex flex-wrap gap-1.5">
                          {projectWorkflows.map((workflow) => (
                            <Link
                              key={workflow.name}
                              to="/jobs/$name"
                              params={{ name: workflow.name }}
                              className="rounded bg-chip px-1.5 py-0.5 text-xs hover:text-link hover:underline"
                            >
                              {workflow.name}
                            </Link>
                          ))}
                        </div>
                      )}
                    </td>
                    {isAdmin && (
                      <td className="px-4 py-2.5 text-right">
                        <button
                          type="button"
                          disabled={projectWorkflows.length > 0}
                          title={projectWorkflows.length > 0 ? t("projects.deleteBlocked") : undefined}
                          onClick={() => void remove(project, projectWorkflows.length)}
                          className="inline-flex items-center gap-1 rounded-md border border-line px-2 py-1 text-xs text-danger hover:bg-danger-subtle disabled:cursor-not-allowed disabled:opacity-40"
                        >
                          <Trash2 size={12} />
                          {t("common.delete")}
                        </button>
                      </td>
                    )}
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
