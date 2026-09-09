import { useEffect, useState } from "react";
import type { HubConnection } from "@microsoft/signalr";

import { Badge } from "@/components/ui/badge";
import { formatTime } from "@/lib/format";
import { getCiHub } from "@/lib/signalr";
import type { AgentInfo } from "@/lib/types";

function formatBytes(bytes: number): string {
  if (bytes <= 0) return "—";
  const units = ["B", "KB", "MB", "GB", "TB"];
  var value = bytes;
  var unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(1)} ${units[unit]}`;
}

export function AgentsPage() {
  const [agents, setAgents] = useState<AgentInfo[]>([]);

  useEffect(() => {
    let cancelled = false;
    let connection: HubConnection | null = null;

    (async () => {
      try {
        connection = await getCiHub();
        if (cancelled) return;
        connection.on("agentsUpdated", (snapshot: AgentInfo[]) => setAgents(snapshot));
        const initial = await connection.invoke<AgentInfo[]>("SubscribeAgents");
        if (!cancelled) setAgents(initial);
      } catch {
        // REST fallback below still renders the last known state.
      }
    })();

    return () => {
      cancelled = true;
      connection?.off("agentsUpdated");
      connection?.invoke("UnsubscribeAgents").catch(() => {});
    };
  }, []);

  return (
    <div className="space-y-4">
      <h2 className="text-sm font-medium text-muted-foreground">Connected agents</h2>
      {agents.length === 0 ? (
        <div className="rounded-lg border bg-card p-8 text-sm text-muted-foreground">
          No agents yet — start one with{" "}
          <code className="rounded bg-muted px-1.5 py-0.5">dotnet run --project src/InfinityCI.Agent</code>.
        </div>
      ) : (
        <div className="overflow-hidden rounded-lg border bg-card">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-xs text-muted-foreground">
                <th className="px-4 py-2 font-medium">Agent</th>
                <th className="px-4 py-2 font-medium">Status</th>
                <th className="px-4 py-2 font-medium">Labels</th>
                <th className="px-4 py-2 font-medium">Version</th>
                <th className="px-4 py-2 font-medium">Builds</th>
                <th className="px-4 py-2 font-medium">CPU</th>
                <th className="px-4 py-2 font-medium">Memory</th>
                <th className="px-4 py-2 font-medium">Free disk</th>
                <th className="px-4 py-2 font-medium">Last seen</th>
              </tr>
            </thead>
            <tbody>
              {agents.map((agent) => (
                <tr key={agent.id} className="border-b last:border-0">
                  <td className="px-4 py-2 font-medium">{agent.name}</td>
                  <td className="px-4 py-2">
                    <Badge variant={agent.online ? "success" : "cancelled"}>
                      {agent.online ? "Online" : "Offline"}
                    </Badge>
                  </td>
                  <td className="px-4 py-2">
                    <div className="flex gap-1">
                      {agent.labels.map((label) => (
                        <Badge key={label} variant="secondary">{label}</Badge>
                      ))}
                    </div>
                  </td>
                  <td className="px-4 py-2 text-muted-foreground">{agent.version}</td>
                  <td className="px-4 py-2 text-muted-foreground">
                    {agent.runningBuilds} / {agent.maxConcurrentBuilds}
                  </td>
                  <td className="px-4 py-2 text-muted-foreground">{agent.cpuPercent}%</td>
                  <td className="px-4 py-2 text-muted-foreground">{agent.memoryPercent}%</td>
                  <td className="px-4 py-2 text-muted-foreground">{formatBytes(agent.freeDiskBytes)}</td>
                  <td className="px-4 py-2 text-muted-foreground">{formatTime(agent.lastSeenUtc)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
