import { useCallback, useEffect, useState } from "react";
import { Plus, Trash2 } from "lucide-react";
import { useTranslation } from "react-i18next";

import { api } from "@/lib/api";
import { useMe } from "@/lib/me-context";
import type { ProjectInfo, UserInfo, UserRole } from "@/lib/types";

export function AdminPage() {
  const { t } = useTranslation();
  const { me } = useMe();
  const isSuperAdmin = me?.role === "SuperAdmin";

  return (
    <div className="space-y-8">
      <h1 className="text-xl font-semibold">{t("admin.title")}</h1>
      <ProjectsSection />
      {isSuperAdmin && <UsersSection />}
    </div>
  );
}

function ProjectsSection() {
  const { t } = useTranslation();
  const [projects, setProjects] = useState<ProjectInfo[]>([]);
  const [name, setName] = useState("");
  const [message, setMessage] = useState<string | null>(null);
  void message;

  const reload = useCallback(async () => {
    try {
      setProjects(await api.projects());
    } catch (e) {
      setMessage(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  return (
    <section>
      <h2 className="mb-2 text-sm font-medium text-fg-muted">{t("admin.projectsTitle")}</h2>
      <div className="rounded-md border border-line bg-canvas p-4">
        <div className="mb-3 flex items-end gap-3">
          <label className="text-sm">
            <span className="mb-1 block text-xs font-medium text-fg-muted">{t("admin.newProjectName")}</span>
            <input
              value={name}
              onChange={(e) => setName(e.target.value)}
              className="w-56 rounded-md border border-line px-2.5 py-1.5 text-sm outline-none focus:border-link"
            />
          </label>
          <button
            type="button"
            disabled={!name.trim()}
            onClick={async () => {
              try {
                await api.createProject(name.trim(), "");
                setName("");
                await reload();
              } catch (e) {
                setMessage(e instanceof Error ? e.message : String(e));
              }
            }}
            className="flex items-center gap-1.5 rounded-md bg-success-btn px-3 py-1.5 text-sm font-medium text-white hover:bg-success-btn-hover disabled:opacity-50"
          >
            <Plus size={14} />
            {t("admin.createProject")}
          </button>
        </div>
        {projects.length === 0 ? (
          <div className="text-sm text-fg-muted">{t("admin.noProjects")}</div>
        ) : (
          <table className="w-full text-sm">
            <tbody>
              {projects.map((project) => (
                <tr key={project.id} className="border-t border-line-muted">
                  <td className="py-2 font-medium">{project.name}</td>
                  <td className="py-2 text-fg-muted">{project.description}</td>
                  <td className="py-2 text-right">
                    <button
                      type="button"
                      onClick={async () => {
                        if (!window.confirm(t("admin.confirmDeleteProject", { name: project.name }))) return;
                        try {
                          await api.deleteProject(project.id);
                          await reload();
                        } catch (e) {
                          setMessage(e instanceof Error ? e.message : String(e));
                        }
                      }}
                      className="inline-flex items-center gap-1 rounded-md border border-line px-2 py-1 text-xs text-danger hover:bg-danger-subtle"
                    >
                      <Trash2 size={12} />
                      {t("common.delete")}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </section>
  );
}

function UsersSection() {
  const { t } = useTranslation();
  const [users, setUsers] = useState<UserInfo[]>([]);
  const [projects, setProjects] = useState<ProjectInfo[]>([]);
  const [message, setMessage] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [form, setForm] = useState<{ username: string; displayName: string; password: string; role: UserRole; projectIds: number[] }>({
    username: "",
    displayName: "",
    password: "",
    role: "User",
    projectIds: [],
  });

  const reload = useCallback(async () => {
    try {
      setUsers(await api.users());
      setProjects(await api.projects());
    } catch (e) {
      setMessage(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  const toggleProject = (projectId: number) => {
    setForm((prev) => ({
      ...prev,
      projectIds: prev.projectIds.includes(projectId)
        ? prev.projectIds.filter((id) => id !== projectId)
        : [...prev.projectIds, projectId],
    }));
  };

  return (
    <section>
      <h2 className="mb-2 text-sm font-medium text-fg-muted">{t("admin.usersTitle")}</h2>
      {message && (
        <div className="mb-3 rounded-md border border-danger-line bg-danger-subtle px-3 py-2 text-sm text-danger">{message}</div>
      )}

      {creating ? (
        <div className="mb-4 rounded-md border border-line bg-canvas p-4">
          <div className="grid gap-3 sm:grid-cols-3">
            <label className="text-sm">
              <span className="mb-1 block text-xs font-medium text-fg-muted">{t("columns.username")}</span>
              <input
                value={form.username}
                onChange={(e) => setForm({ ...form, username: e.target.value })}
                className="w-full rounded-md border border-line px-2.5 py-1.5 text-sm outline-none focus:border-link"
              />
            </label>
            <label className="text-sm">
              <span className="mb-1 block text-xs font-medium text-fg-muted">{t("columns.displayName")}</span>
              <input
                value={form.displayName}
                onChange={(e) => setForm({ ...form, displayName: e.target.value })}
                className="w-full rounded-md border border-line px-2.5 py-1.5 text-sm outline-none focus:border-link"
              />
            </label>
            <label className="text-sm">
              <span className="mb-1 block text-xs font-medium text-fg-muted">{t("login.password")}</span>
              <input
                type="password"
                value={form.password}
                onChange={(e) => setForm({ ...form, password: e.target.value })}
                className="w-full rounded-md border border-line px-2.5 py-1.5 text-sm outline-none focus:border-link"
              />
            </label>
            <label className="text-sm">
              <span className="mb-1 block text-xs font-medium text-fg-muted">{t("columns.role")}</span>
              <select
                value={form.role}
                onChange={(e) => setForm({ ...form, role: e.target.value as UserRole })}
                className="w-full rounded-md border border-line px-2.5 py-1.5 text-sm outline-none focus:border-link"
              >
                <option value="User">{t("admin.roleUser")}</option>
                <option value="Admin">{t("admin.roleAdmin")}</option>
                <option value="SuperAdmin">{t("admin.roleSuperAdmin")}</option>
              </select>
            </label>
          </div>
          <div className="mt-3">
            <span className="mb-1 block text-xs font-medium text-fg-muted">{t("columns.visibleProjects")}</span>
            <div className="flex flex-wrap gap-3">
              {projects.map((project) => (
                <label key={project.id} className="flex items-center gap-1.5 text-sm">
                  <input
                    type="checkbox"
                    checked={form.projectIds.includes(project.id)}
                    onChange={() => toggleProject(project.id)}
                  />
                  {project.name}
                </label>
              ))}
            </div>
          </div>
          <div className="mt-3 flex gap-2">
            <button
              type="button"
              disabled={!form.username || !form.password}
              onClick={async () => {
                try {
                  await api.createUser(form.username, form.password, form.role, form.projectIds, form.displayName);
                  setCreating(false);
                  setForm({ username: "", displayName: "", password: "", role: "User", projectIds: [] });
                  await reload();
                } catch (e) {
                  setMessage(e instanceof Error ? e.message : String(e));
                }
              }}
              className="rounded-md bg-success-btn px-3 py-1.5 text-sm font-medium text-white hover:bg-success-btn-hover disabled:opacity-50"
            >
              {t("admin.createUser")}
            </button>
            <button
              type="button"
              onClick={() => setCreating(false)}
              className="rounded-md border border-line px-3 py-1.5 text-sm hover:bg-canvas-subtle"
            >
              {t("common.cancel")}
            </button>
          </div>
        </div>
      ) : (
        <button
          type="button"
          onClick={() => setCreating(true)}
          className="mb-3 flex items-center gap-1.5 rounded-md bg-success-btn px-3 py-1.5 text-sm font-medium text-white hover:bg-success-btn-hover"
        >
          <Plus size={14} />
          {t("admin.newUser")}
        </button>
      )}

      <div className="overflow-hidden rounded-md border border-line bg-canvas">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-line bg-canvas-subtle text-left text-xs text-fg-muted">
              <th className="px-4 py-2">{t("columns.username")}</th>
              <th className="px-4 py-2">{t("columns.displayName")}</th>
              <th className="px-4 py-2">{t("columns.role")}</th>
              <th className="px-4 py-2">{t("columns.visibleProjects")}</th>
              <th className="px-4 py-2 text-right">{t("columns.actions")}</th>
            </tr>
          </thead>
          <tbody>
            {users.map((user) => (
              <tr key={user.id} className="border-b border-line-muted last:border-0">
                <td className="px-4 py-2 font-medium">
                  {user.username}
                  {user.hasPassword === false && (
                    <span className="ml-1.5 rounded bg-link-subtle px-1.5 py-0.5 text-xs font-normal text-link">
                      LDAP
                    </span>
                  )}
                </td>
                <td className="px-4 py-2 text-fg-muted">{user.displayName ?? "—"}</td>
                <td className="px-4 py-2">
                  <select
                    value={user.role}
                    onChange={async (e) => {
                      try {
                        await api.updateUser(user.id, { role: e.target.value });
                        await reload();
                      } catch (err) {
                        setMessage(err instanceof Error ? err.message : String(err));
                      }
                    }}
                    className="rounded border border-line px-2 py-1 text-xs outline-none focus:border-link"
                  >
                    <option value="User">{t("admin.roleUser")}</option>
                    <option value="Admin">{t("admin.roleAdmin")}</option>
                    <option value="SuperAdmin">{t("admin.roleSuperAdmin")}</option>
                  </select>
                </td>
                <td className="px-4 py-2">
                  <div className="flex flex-wrap gap-1">
                    {user.projects.map((project) => (
                      <span key={project.projectId} className="rounded bg-chip px-1.5 py-0.5 text-xs">
                        {project.name}
                      </span>
                    ))}
                    {user.projects.length === 0 && <span className="text-xs text-fg-muted">{t("admin.allOrNone")}</span>}
                  </div>
                  <div className="mt-1 flex flex-wrap gap-1">
                    {projects
                      .filter((p) => !user.projects.some((up) => up.projectId === p.id))
                      .map((project) => (
                        <button
                          key={project.id}
                          type="button"
                          onClick={async () => {
                            const ids = [...new Set([...user.projects.map((up) => up.projectId), project.id])];
                            try {
                              await api.updateUser(user.id, { projectIds: ids });
                              await reload();
                            } catch (err) {
                              setMessage(err instanceof Error ? err.message : String(err));
                            }
                          }}
                          className="rounded border border-dashed border-line px-1.5 py-0.5 text-xs text-link hover:bg-link-subtle"
                        >
                          + {project.name}
                        </button>
                      ))}
                    {user.projects.length > 0 &&
                      user.projects.map((up) => (
                        <button
                          key={`remove-${up.projectId}`}
                          type="button"
                          onClick={async () => {
                            const ids = user.projects.filter((p) => p.projectId !== up.projectId).map((p) => p.projectId);
                            try {
                              await api.updateUser(user.id, { projectIds: ids });
                              await reload();
                            } catch (err) {
                              setMessage(err instanceof Error ? err.message : String(err));
                            }
                          }}
                          className="rounded border border-dashed border-danger-line px-1.5 py-0.5 text-xs text-danger hover:bg-danger-subtle"
                        >
                          − {projects.find((p) => p.id === up.projectId)?.name ?? up.projectId}
                        </button>
                      ))}
                  </div>
                </td>
                <td className="px-4 py-2 text-right">
                    <button
                      type="button"
                      onClick={async () => {
                        if (!window.confirm(t("admin.confirmDeleteUser", { username: user.username }))) return;
                        try {
                          await api.deleteUser(user.id);
                          await reload();
                        } catch (err) {
                          setMessage(err instanceof Error ? err.message : String(err));
                        }
                      }}
                      className="inline-flex items-center gap-1 rounded-md border border-line px-2 py-1 text-xs text-danger hover:bg-danger-subtle"
                    >
                      <Trash2 size={12} />
                      {t("common.delete")}
                    </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
