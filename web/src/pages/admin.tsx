import { useCallback, useEffect, useState } from "react";
import { Plus, Trash2 } from "lucide-react";

import { api } from "@/lib/api";
import { useMe } from "@/lib/me-context";
import type { ProjectInfo, UserInfo, UserRole } from "@/lib/types";

export function AdminPage() {
  const { me } = useMe();
  const isSuperAdmin = me?.role === "SuperAdmin";

  return (
    <div className="space-y-8">
      <h1 className="text-xl font-semibold">管理</h1>
      <ProjectsSection />
      {isSuperAdmin && <UsersSection />}
    </div>
  );
}

function ProjectsSection() {
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
      <h2 className="mb-2 text-sm font-medium text-[#57606a]">项目</h2>
      <div className="rounded-md border border-[#d0d7de] bg-white p-4">
        <div className="mb-3 flex items-end gap-3">
          <label className="text-sm">
            <span className="mb-1 block text-xs font-medium text-[#57606a]">新项目名称</span>
            <input
              value={name}
              onChange={(e) => setName(e.target.value)}
              className="w-56 rounded-md border border-[#d0d7de] px-2.5 py-1.5 text-sm outline-none focus:border-[#0969da]"
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
            className="flex items-center gap-1.5 rounded-md bg-[#2da44e] px-3 py-1.5 text-sm font-medium text-white hover:bg-[#2c974b] disabled:opacity-50"
          >
            <Plus size={14} />
            创建项目
          </button>
        </div>
        {projects.length === 0 ? (
          <div className="text-sm text-[#57606a]">暂无项目。</div>
        ) : (
          <table className="w-full text-sm">
            <tbody>
              {projects.map((project) => (
                <tr key={project.id} className="border-t border-[#eaeef2]">
                  <td className="py-2 font-medium">{project.name}</td>
                  <td className="py-2 text-[#57606a]">{project.description}</td>
                  <td className="py-2 text-right">
                    <button
                      type="button"
                      onClick={async () => {
                        if (!window.confirm(`删除项目「${project.name}」？`)) return;
                        try {
                          await api.deleteProject(project.id);
                          await reload();
                        } catch (e) {
                          setMessage(e instanceof Error ? e.message : String(e));
                        }
                      }}
                      className="inline-flex items-center gap-1 rounded-md border border-[#d0d7de] px-2 py-1 text-xs text-[#cf222e] hover:bg-[#ffebe9]"
                    >
                      <Trash2 size={12} />
                      删除
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
      <h2 className="mb-2 text-sm font-medium text-[#57606a]">用户（超级管理员）</h2>
      {message && (
        <div className="mb-3 rounded-md border border-[#ffc1bc] bg-[#ffebe9] px-3 py-2 text-sm text-[#cf222e]">{message}</div>
      )}

      {creating ? (
        <div className="mb-4 rounded-md border border-[#d0d7de] bg-white p-4">
          <div className="grid gap-3 sm:grid-cols-3">
            <label className="text-sm">
              <span className="mb-1 block text-xs font-medium text-[#57606a]">用户名</span>
              <input
                value={form.username}
                onChange={(e) => setForm({ ...form, username: e.target.value })}
                className="w-full rounded-md border border-[#d0d7de] px-2.5 py-1.5 text-sm outline-none focus:border-[#0969da]"
              />
            </label>
            <label className="text-sm">
              <span className="mb-1 block text-xs font-medium text-[#57606a]">姓名</span>
              <input
                value={form.displayName}
                onChange={(e) => setForm({ ...form, displayName: e.target.value })}
                className="w-full rounded-md border border-[#d0d7de] px-2.5 py-1.5 text-sm outline-none focus:border-[#0969da]"
              />
            </label>
            <label className="text-sm">
              <span className="mb-1 block text-xs font-medium text-[#57606a]">密码</span>
              <input
                type="password"
                value={form.password}
                onChange={(e) => setForm({ ...form, password: e.target.value })}
                className="w-full rounded-md border border-[#d0d7de] px-2.5 py-1.5 text-sm outline-none focus:border-[#0969da]"
              />
            </label>
            <label className="text-sm">
              <span className="mb-1 block text-xs font-medium text-[#57606a]">角色</span>
              <select
                value={form.role}
                onChange={(e) => setForm({ ...form, role: e.target.value as UserRole })}
                className="w-full rounded-md border border-[#d0d7de] px-2.5 py-1.5 text-sm outline-none focus:border-[#0969da]"
              >
                <option value="User">普通用户</option>
                <option value="Admin">管理员</option>
                <option value="SuperAdmin">超级管理员</option>
              </select>
            </label>
          </div>
          <div className="mt-3">
            <span className="mb-1 block text-xs font-medium text-[#57606a]">可见项目</span>
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
              className="rounded-md bg-[#2da44e] px-3 py-1.5 text-sm font-medium text-white hover:bg-[#2c974b] disabled:opacity-50"
            >
              创建用户
            </button>
            <button
              type="button"
              onClick={() => setCreating(false)}
              className="rounded-md border border-[#d0d7de] px-3 py-1.5 text-sm hover:bg-[#f6f8fa]"
            >
              取消
            </button>
          </div>
        </div>
      ) : (
        <button
          type="button"
          onClick={() => setCreating(true)}
          className="mb-3 flex items-center gap-1.5 rounded-md bg-[#2da44e] px-3 py-1.5 text-sm font-medium text-white hover:bg-[#2c974b]"
        >
          <Plus size={14} />
          新建用户
        </button>
      )}

      <div className="overflow-hidden rounded-md border border-[#d0d7de] bg-white">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-[#d0d7de] bg-[#f6f8fa] text-left text-xs text-[#57606a]">
              <th className="px-4 py-2">用户名</th>
              <th className="px-4 py-2">角色</th>
              <th className="px-4 py-2">可见项目</th>
              <th className="px-4 py-2 text-right">操作</th>
            </tr>
          </thead>
          <tbody>
            {users.map((user) => (
              <tr key={user.id} className="border-b border-[#d8dee4] last:border-0">
                <td className="px-4 py-2 font-medium">{user.username}</td>
                <td className="px-4 py-2 text-[#57606a]">{user.displayName ?? "—"}</td>
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
                    className="rounded border border-[#d0d7de] px-2 py-1 text-xs outline-none focus:border-[#0969da]"
                  >
                    <option value="User">普通用户</option>
                    <option value="Admin">管理员</option>
                    <option value="SuperAdmin">超级管理员</option>
                  </select>
                </td>
                <td className="px-4 py-2">
                  <div className="flex flex-wrap gap-1">
                    {user.projects.map((project) => (
                      <span key={project.projectId} className="rounded bg-[#eaeef2] px-1.5 py-0.5 text-xs">
                        {project.name}
                      </span>
                    ))}
                    {user.projects.length === 0 && <span className="text-xs text-[#57606a]">（全部或无）</span>}
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
                          className="rounded border border-dashed border-[#d0d7de] px-1.5 py-0.5 text-xs text-[#0969da] hover:bg-[#ddf4ff]"
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
                          className="rounded border border-dashed border-[#ffc1bc] px-1.5 py-0.5 text-xs text-[#cf222e] hover:bg-[#ffebe9]"
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
                      if (!window.confirm(`删除用户「${user.username}」？`)) return;
                      try {
                        await api.deleteUser(user.id);
                        await reload();
                      } catch (err) {
                        setMessage(err instanceof Error ? err.message : String(err));
                      }
                    }}
                    className="inline-flex items-center gap-1 rounded-md border border-[#d0d7de] px-2 py-1 text-xs text-[#cf222e] hover:bg-[#ffebe9]"
                  >
                    <Trash2 size={12} />
                    删除
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
