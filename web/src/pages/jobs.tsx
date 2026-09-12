import { useEffect, useState } from "react";
import { Link } from "@tanstack/react-router";
import { Copy, History, Pencil, Play, Plus, Trash2 } from "lucide-react";

import { HistoryDrawer } from "@/components/history-drawer";
import { api } from "@/lib/api";
import { useMe } from "@/lib/me-context";
import type { WorkflowInfo } from "@/lib/types";

export function JobsPage() {
  const { me } = useMe();
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
    if (!window.confirm(`删除任务「${name}」？`)) return;
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
          <h1 className="text-xl font-semibold">任务</h1>
          <p className="mt-0.5 text-xs text-[#57606a]">
            配置存放在 Git 仓库中，可只读克隆：{" "}
            <code className="rounded bg-[#eff2f5] px-1.5 py-0.5 font-mono text-xs">
              git clone http://用户:密码@host:5000/git/jobs
            </code>
          </p>
        </div>
        {isAdmin && (
          <Link
            to="/jobs/new"
            className="flex items-center gap-1.5 rounded-md bg-[#2da44e] px-3 py-1.5 text-sm font-medium text-white hover:bg-[#2c974b]"
          >
            <Plus size={14} />
            新建任务
          </Link>
        )}
      </div>

      {message && (
        <div className="rounded-md border border-[#ffc1bc] bg-[#ffebe9] px-3 py-2 text-sm text-[#cf222e]">{message}</div>
      )}

      {loading ? (
        <div className="text-sm text-[#57606a]">加载中…</div>
      ) : workflows.length === 0 ? (
        <div className="rounded-md border border-[#d0d7de] bg-white p-6 text-sm text-[#57606a]">
          还没有任务。{isAdmin ? "点击右上角「新建任务」创建。" : "请联系管理员创建。"}
        </div>
      ) : (
        <div className="overflow-hidden rounded-md border border-[#d0d7de] bg-white">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-[#d0d7de] bg-[#f6f8fa] text-left text-xs text-[#57606a]">
                <th className="px-4 py-2">名称</th>
                <th className="px-4 py-2">项目</th>
                <th className="px-4 py-2">Jobs</th>
                <th className="px-4 py-2 text-right">操作</th>
              </tr>
            </thead>
            <tbody>
              {workflows.map((workflow) => (
                <tr key={workflow.name} className="border-b border-[#d8dee4] last:border-0 hover:bg-[#f6f8fa]">
                  <td className="px-4 py-2.5 font-medium">
                    <Link to="/jobs/$name" params={{ name: workflow.name }} className="hover:text-[#0969da] hover:underline">
                      {workflow.name}
                    </Link>
                  </td>
                  <td className="px-4 py-2.5 text-[#57606a]">{workflow.project}</td>
                  <td className="px-4 py-2.5 text-[#57606a]">
                    <div className="flex flex-wrap gap-1.5">
                      {workflow.jobs.map((job) => (
                        <span key={job.key} className="rounded bg-[#eaeef2] px-1.5 py-0.5 text-xs">
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
                        className="flex items-center gap-1 rounded-md bg-[#2da44e] px-2 py-1 text-xs text-white hover:bg-[#2c974b]"
                      >
                        <Play size={12} />
                        Run
                      </button>
                      <button
                        type="button"
                        onClick={() => setHistoryFor(workflow.name)}
                        className="flex items-center gap-1 rounded-md border border-[#d0d7de] px-2 py-1 text-xs hover:bg-[#f3f4f6]"
                      >
                        <History size={12} />
                        历史
                      </button>
                      {isAdmin && (
                        <>
                          <Link
                            to="/jobs/$name/edit"
                            params={{ name: workflow.name }}
                            className="flex items-center gap-1 rounded-md border border-[#d0d7de] px-2 py-1 text-xs hover:bg-[#f3f4f6]"
                          >
                            <Pencil size={12} />
                            编辑
                          </Link>
                          <Link
                            to="/jobs/new"
                            search={{ from: workflow.name }}
                            className="flex items-center gap-1 rounded-md border border-[#d0d7de] px-2 py-1 text-xs hover:bg-[#f3f4f6]"
                          >
                            <Copy size={12} />
                            复制
                          </Link>
                          <button
                            type="button"
                            onClick={() => void remove(workflow.name)}
                            className="flex items-center gap-1 rounded-md border border-[#d0d7de] px-2 py-1 text-xs text-[#cf222e] hover:bg-[#ffebe9]"
                          >
                            <Trash2 size={12} />
                            删除
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
