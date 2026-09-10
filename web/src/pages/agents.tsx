import { useCallback, useEffect, useState } from "react";
import type { HubConnection } from "@microsoft/signalr";
import { Copy, KeyRound, Power, Trash2 } from "lucide-react";

import { StatusIcon } from "@/components/status-icon";
import { api } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import type { CredentialInfo } from "@/lib/types";
import { getCiHub } from "@/lib/signalr";
import type { AgentInfo } from "@/lib/types";

export function AgentsPage() {
  const [live, setLive] = useState<AgentInfo[]>([]);
  const [enrolled, setEnrolled] = useState<Awaited<ReturnType<typeof api.enrolledAgents>>>([]);
  const [enrollments, setEnrollments] = useState<Awaited<ReturnType<typeof api.enrollments>>>([]);
  const [message, setMessage] = useState<string | null>(null);
  const [newEnrollment, setNewEnrollment] = useState<{ name: string; labels: string; max: number }>({
    name: "",
    labels: "",
    max: 1,
  });
  const [lastCommand, setLastCommand] = useState<string | null>(null);

  const reload = useCallback(async () => {
    try {
      setEnrolled(await api.enrolledAgents());
      setEnrollments(await api.enrollments());
    } catch (e) {
      setMessage(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => {
    let cancelled = false;
    let connection: HubConnection | null = null;

    void reload();
    api
      .agents()
      .then(setLive)
      .catch(() => {});

    (async () => {
      try {
        connection = await getCiHub();
        if (cancelled) return;
        connection.on("agentsUpdated", (snapshot: AgentInfo[]) => setLive(snapshot));
      } catch {
        // REST only
      }
    })();

    return () => {
      cancelled = true;
      connection?.off("agentsUpdated");
      connection?.invoke("UnsubscribeAgents").catch(() => {});
    };
  }, [reload]);

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-semibold">Agents</h1>
      {message && (
        <div className="rounded-md border border-[#ffc1bc] bg-[#ffebe9] px-3 py-2 text-sm text-[#cf222e]">{message}</div>
      )}

      {/* Live agents */}
      <section>
        <h2 className="mb-2 text-sm font-medium text-[#57606a]">在线状态</h2>
        {live.length === 0 ? (
          <div className="rounded-md border border-[#d0d7de] bg-white p-5 text-sm text-[#57606a]">
            没有在线 Agent — 在下方签发注册令牌并启动 Agent 进程。
          </div>
        ) : (
          <div className="overflow-hidden rounded-md border border-[#d0d7de] bg-white">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-[#d0d7de] bg-[#f6f8fa] text-left text-xs text-[#57606a]">
                  <th className="px-4 py-2">Agent</th>
                  <th className="px-4 py-2">状态</th>
                  <th className="px-4 py-2">版本</th>
                  <th className="px-4 py-2">Jobs</th>
                  <th className="px-4 py-2">CPU</th>
                  <th className="px-4 py-2">内存</th>
                </tr>
              </thead>
              <tbody>
                {live.map((agent) => (
                  <tr key={agent.id} className="border-b border-[#d8dee4] last:border-0">
                    <td className="px-4 py-2 font-medium">{agent.name}</td>
                    <td className="px-4 py-2">
                      <span className="flex items-center gap-1.5">
                        <StatusIcon status={agent.online ? "Success" : "Cancelled"} size={12} />
                        {agent.online ? "Online" : "Offline"}
                      </span>
                    </td>
                    <td className="px-4 py-2 text-[#57606a]">{agent.version}</td>
                    <td className="px-4 py-2 text-[#57606a]">
                      {agent.runningBuilds} / {agent.maxConcurrentBuilds}
                    </td>
                    <td className="px-4 py-2 text-[#57606a]">{agent.cpuPercent}%</td>
                    <td className="px-4 py-2 text-[#57606a]">{agent.memoryPercent}%</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      {/* Enrollment */}
      <section>
        <h2 className="mb-2 text-sm font-medium text-[#57606a]">注册新 Agent</h2>
        <div className="rounded-md border border-[#d0d7de] bg-white p-4">
          <div className="flex flex-wrap items-end gap-3">
            <label className="text-sm">
              <span className="mb-1 block text-xs font-medium text-[#57606a]">名称</span>
              <input
                value={newEnrollment.name}
                onChange={(e) => setNewEnrollment({ ...newEnrollment, name: e.target.value })}
                placeholder="例如 build-agent-1"
                className="w-44 rounded-md border border-[#d0d7de] px-2.5 py-1.5 text-sm outline-none focus:border-[#0969da]"
              />
            </label>
            <label className="text-sm">
              <span className="mb-1 block text-xs font-medium text-[#57606a]">标签（逗号分隔）</span>
              <input
                value={newEnrollment.labels}
                onChange={(e) => setNewEnrollment({ ...newEnrollment, labels: e.target.value })}
                placeholder="docker, linux"
                className="w-52 rounded-md border border-[#d0d7de] px-2.5 py-1.5 text-sm outline-none focus:border-[#0969da]"
              />
            </label>
            <label className="text-sm">
              <span className="mb-1 block text-xs font-medium text-[#57606a]">最大并发</span>
              <input
                type="number"
                min={1}
                value={newEnrollment.max}
                onChange={(e) => setNewEnrollment({ ...newEnrollment, max: Number(e.target.value) || 1 })}
                className="w-20 rounded-md border border-[#d0d7de] px-2.5 py-1.5 text-sm outline-none focus:border-[#0969da]"
              />
            </label>
            <button
              type="button"
              disabled={!newEnrollment.name}
              onClick={async () => {
                try {
                  const labels = newEnrollment.labels
                    .split(",")
                    .map((l) => l.trim())
                    .filter(Boolean);
                  const created = await api.createEnrollment(newEnrollment.name, labels, newEnrollment.max);
                  setLastCommand(created.command);
                  await reload();
                } catch (e) {
                  setMessage(e instanceof Error ? e.message : String(e));
                }
              }}
              className="rounded-md bg-[#2da44e] px-3 py-1.5 text-sm font-medium text-white hover:bg-[#2c974b] disabled:opacity-50"
            >
              签发注册令牌
            </button>
          </div>
          {lastCommand && (
            <div className="mt-3 rounded-md bg-[#0d1117] p-3 font-mono text-xs text-[#c9d1d9]">
              <div className="mb-1 flex items-center justify-between text-[#7d8590]">
                <span>在目标机器上执行：</span>
                <button
                  type="button"
                  onClick={() => void navigator.clipboard.writeText(lastCommand)}
                  className="flex items-center gap-1 hover:text-white"
                >
                  <Copy size={12} />
                  复制
                </button>
              </div>
              {lastCommand}
            </div>
          )}
        </div>

        {enrollments.length > 0 && (
          <div className="mt-3 overflow-hidden rounded-md border border-[#d0d7de] bg-white">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-[#d0d7de] bg-[#f6f8fa] text-left text-xs text-[#57606a]">
                  <th className="px-4 py-2">名称</th>
                  <th className="px-4 py-2">令牌</th>
                  <th className="px-4 py-2">状态</th>
                  <th className="px-4 py-2">创建时间</th>
                  <th className="px-4 py-2 text-right">操作</th>
                </tr>
              </thead>
              <tbody>
                {enrollments.map((enrollment) => (
                  <tr key={enrollment.id} className="border-b border-[#d8dee4] last:border-0">
                    <td className="px-4 py-2">{enrollment.name}</td>
                    <td className="px-4 py-2 font-mono text-xs text-[#57606a]">
                      {enrollment.token.slice(0, 10)}…
                    </td>
                    <td className="px-4 py-2">
                      {enrollment.usedByAgentId ? (
                        <span className="text-[#57606a]">已使用（{enrollment.usedByAgentId.slice(0, 8)}…）</span>
                      ) : (
                        <span className="text-[#1a7f37]">待使用</span>
                      )}
                    </td>
                    <td className="px-4 py-2 text-[#57606a]">{formatDateTime(enrollment.createdUtc)}</td>
                    <td className="px-4 py-2 text-right">
                      <button
                        type="button"
                        onClick={async () => {
                          await api.deleteEnrollment(enrollment.id);
                          await reload();
                        }}
                        className="inline-flex items-center gap-1 rounded-md border border-[#d0d7de] px-2 py-1 text-xs text-[#cf222e] hover:bg-[#ffebe9]"
                      >
                        <Trash2 size={12} />
                        撤销
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      {/* Git credentials */}
      <CredentialsSection onMessage={setMessage} />

      {/* Enrolled agents (persistent) */}
      <section>
        <h2 className="mb-2 text-sm font-medium text-[#57606a]">已注册 Agent</h2>
        {enrolled.length === 0 ? (
          <div className="rounded-md border border-[#d0d7de] bg-white p-5 text-sm text-[#57606a]">
            尚无已注册 Agent。
          </div>
        ) : (
          <div className="overflow-hidden rounded-md border border-[#d0d7de] bg-white">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-[#d0d7de] bg-[#f6f8fa] text-left text-xs text-[#57606a]">
                  <th className="px-4 py-2">名称</th>
                  <th className="px-4 py-2">状态</th>
                  <th className="px-4 py-2">标签</th>
                  <th className="px-4 py-2">注册时间</th>
                  <th className="px-4 py-2">最后在线</th>
                  <th className="px-4 py-2 text-right">操作</th>
                </tr>
              </thead>
              <tbody>
                {enrolled.map((agent) => (
                  <tr key={agent.id} className="border-b border-[#d8dee4] last:border-0">
                    <td className="px-4 py-2 font-medium">{agent.name}</td>
                    <td className="px-4 py-2">
                      {!agent.enabled ? (
                        <span className="rounded bg-[#eaeef2] px-1.5 py-0.5 text-xs">已禁用</span>
                      ) : agent.online ? (
                        <span className="flex items-center gap-1 text-[#1a7f37]">
                          <StatusIcon status="Success" size={12} />
                          Online
                        </span>
                      ) : (
                        <span className="text-[#57606a]">Offline</span>
                      )}
                    </td>
                    <td className="px-4 py-2">
                      <div className="flex gap-1">
                        {agent.labels.map((label) => (
                          <span key={label} className="rounded bg-[#eaeef2] px-1.5 py-0.5 text-xs">
                            {label}
                          </span>
                        ))}
                      </div>
                    </td>
                    <td className="px-4 py-2 text-[#57606a]">{formatDateTime(agent.enrolledAt)}</td>
                    <td className="px-4 py-2 text-[#57606a]">{formatDateTime(agent.lastSeenUtc)}</td>
                    <td className="px-4 py-2">
                      <div className="flex items-center justify-end gap-2">
                        <button
                          type="button"
                          onClick={async () => {
                            await api.setAgentEnabled(agent.id, !agent.enabled);
                            await reload();
                          }}
                          className="flex items-center gap-1 rounded-md border border-[#d0d7de] px-2 py-1 text-xs hover:bg-[#f3f4f6]"
                        >
                          <Power size={12} />
                          {agent.enabled ? "禁用" : "启用"}
                        </button>
                        <button
                          type="button"
                          onClick={async () => {
                            if (!window.confirm(`删除 Agent「${agent.name}」？`)) return;
                            await api.deleteAgent(agent.id);
                            await reload();
                          }}
                          className="flex items-center gap-1 rounded-md border border-[#d0d7de] px-2 py-1 text-xs text-[#cf222e] hover:bg-[#ffebe9]"
                        >
                          <Trash2 size={12} />
                          删除
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}


function CredentialsSection({ onMessage }: { onMessage: (message: string) => void }) {
  const [credentials, setCredentials] = useState<CredentialInfo[]>([]);
  const [form, setForm] = useState<{ name: string; username: string; secret: string }>({
    name: "",
    username: "",
    secret: "",
  });

  const reload = useCallback(async () => {
    try {
      setCredentials(await api.credentials());
    } catch {
      setCredentials([]);
    }
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  return (
    <section>
      <h2 className="mb-2 flex items-center gap-1.5 text-sm font-medium text-[#57606a]">
        <KeyRound size={13} />
        凭据管理（Git SCM，管理员）
      </h2>
      <div className="rounded-md border border-[#d0d7de] bg-white p-4">
        <div className="mb-3 flex flex-wrap items-end gap-3">
          <label className="text-sm">
            <span className="mb-1 block text-xs font-medium text-[#57606a]">名称</span>
            <input
              value={form.name}
              onChange={(e) => setForm({ ...form, name: e.target.value })}
              placeholder="deploy-key"
              className="w-40 rounded-md border border-[#d0d7de] px-2.5 py-1.5 text-sm outline-none focus:border-[#0969da]"
            />
          </label>
          <label className="text-sm">
            <span className="mb-1 block text-xs font-medium text-[#57606a]">用户名 / Token</span>
            <input
              value={form.username}
              onChange={(e) => setForm({ ...form, username: e.target.value })}
              placeholder="git"
              className="w-44 rounded-md border border-[#d0d7de] px-2.5 py-1.5 text-sm outline-none focus:border-[#0969da]"
            />
          </label>
          <label className="text-sm">
            <span className="mb-1 block text-xs font-medium text-[#57606a]">密码 / Token</span>
            <input
              type="password"
              value={form.secret}
              onChange={(e) => setForm({ ...form, secret: e.target.value })}
              className="w-52 rounded-md border border-[#d0d7de] px-2.5 py-1.5 text-sm outline-none focus:border-[#0969da]"
            />
          </label>
          <button
            type="button"
            disabled={!form.name || !form.username}
            onClick={async () => {
              try {
                await api.saveCredential(form.name, form.username, form.secret);
                setForm({ name: "", username: "", secret: "" });
                await reload();
              } catch (e) {
                onMessage(e instanceof Error ? e.message : String(e));
              }
            }}
            className="rounded-md bg-[#2da44e] px-3 py-1.5 text-sm font-medium text-white hover:bg-[#2c974b] disabled:opacity-50"
          >
            保存凭据
          </button>
        </div>
        {credentials.length === 0 ? (
          <div className="text-sm text-[#57606a]">尚无凭据。公开仓库无需凭据。</div>
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-[#d0d7de] text-left text-xs text-[#57606a]">
                <th className="py-1.5">名称</th>
                <th className="py-1.5">用户名</th>
                <th className="py-1.5">创建时间</th>
                <th className="py-1.5 text-right">操作</th>
              </tr>
            </thead>
            <tbody>
              {credentials.map((credential) => (
                <tr key={credential.id} className="border-t border-[#eaeef2]">
                  <td className="py-2 font-medium">{credential.name}</td>
                  <td className="py-2 text-[#57606a]">{credential.username}</td>
                  <td className="py-2 text-[#57606a]">{formatDateTime(credential.createdUtc)}</td>
                  <td className="py-2 text-right">
                    <button
                      type="button"
                      onClick={async () => {
                        if (!window.confirm(`删除凭据「${credential.name}」？`)) return;
                        try {
                          await api.deleteCredential(credential.name);
                          await reload();
                        } catch (e) {
                          onMessage(e instanceof Error ? e.message : String(e));
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
