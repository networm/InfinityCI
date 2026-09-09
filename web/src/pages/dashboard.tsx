import { useEffect, useState } from "react";
import { Link } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { create } from "zustand";
import type { HubConnection } from "@microsoft/signalr";
import { Play } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";
import { buildStatusVariant, formatDuration, formatTime } from "@/lib/format";
import { getCiHub } from "@/lib/signalr";
import type { Build } from "@/lib/types";

interface DashboardState {
  builds: Build[];
  setBuilds: (builds: Build[]) => void;
  applyUpdate: (build: Build) => void;
}

/** Tab-local dashboard feed; version guards against out-of-order pushes. */
const useDashboardStore = create<DashboardState>((set) => ({
  builds: [],
  setBuilds: (builds) => set({ builds }),
  applyUpdate: (build) =>
    set((state) => {
      const existing = state.builds.find((b) => b.id === build.id);
      if (existing && existing.version > build.version) return state;
      const builds = existing
        ? state.builds.map((b) => (b.id === build.id ? build : b))
        : [build, ...state.builds].slice(0, 100);
      return { builds };
    }),
}));

function useDashboardFeed() {
  const setBuilds = useDashboardStore((s) => s.setBuilds);
  const applyUpdate = useDashboardStore((s) => s.applyUpdate);

  useEffect(() => {
    let cancelled = false;
    let connection: HubConnection | null = null;

    (async () => {
      try {
        connection = await getCiHub();
        if (cancelled) return;
        connection.on("buildUpdated", applyUpdate);
        const initial = await connection.invoke<Build[]>("SubscribeDashboard");
        if (!cancelled) setBuilds(initial);
        connection.on("reconnected", async () => {
          // Rejoining is idempotent; the snapshot heals anything missed while offline.
          const fresh = await connection!.invoke<Build[]>("SubscribeDashboard");
          if (!cancelled) setBuilds(fresh);
        });
      } catch {
        // Feed stays empty; REST data from the query below still renders.
      }
    })();

    return () => {
      cancelled = true;
      connection?.off("buildUpdated", applyUpdate);
      connection?.invoke("UnsubscribeDashboard").catch(() => {});
    };
  }, [applyUpdate, setBuilds]);
}

export function DashboardPage() {
  useDashboardFeed();
  const builds = useDashboardStore((s) => s.builds);
  const jobsQuery = useQuery({ queryKey: ["jobs"], queryFn: api.jobs });
  const [triggering, setTriggering] = useState<string | null>(null);

  const sorted = [...builds].sort((a, b) => b.id - a.id);

  return (
    <div className="space-y-6">
      <section>
        <h2 className="mb-3 text-sm font-medium text-muted-foreground">Jobs</h2>
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {jobsQuery.isLoading && <div className="text-sm text-muted-foreground">Loading…</div>}
          {jobsQuery.data?.map((job) => (
            <div key={job.name} className="flex items-center justify-between rounded-lg border bg-card p-3">
              <div>
                <div className="text-sm font-medium">{job.name}</div>
                <div className="text-xs text-muted-foreground">{job.description ?? `${job.steps.length} step(s)`}</div>
              </div>
              <Button
                size="sm"
                variant="outline"
                disabled={triggering === job.name}
                onClick={async () => {
                  setTriggering(job.name);
                  try {
                    await api.trigger(job.name);
                  } finally {
                    setTriggering(null);
                  }
                }}
              >
                <Play />
                Run
              </Button>
            </div>
          ))}
          {jobsQuery.data?.length === 0 && (
            <div className="text-sm text-muted-foreground">
              No jobs yet — drop a YAML file into <code>data/jobs/</code>.
            </div>
          )}
        </div>
      </section>

      <section>
        <h2 className="mb-3 text-sm font-medium text-muted-foreground">Recent builds</h2>
        {sorted.length === 0 ? (
          <div className="rounded-lg border bg-card p-8 text-sm text-muted-foreground">
            No builds yet — press Run on a job above.
          </div>
        ) : (
          <div className="overflow-hidden rounded-lg border bg-card">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-xs text-muted-foreground">
                  <th className="px-4 py-2 font-medium">Build</th>
                  <th className="px-4 py-2 font-medium">Job</th>
                  <th className="px-4 py-2 font-medium">Status</th>
                  <th className="px-4 py-2 font-medium">Created</th>
                  <th className="px-4 py-2 font-medium">Duration</th>
                </tr>
              </thead>
              <tbody>
                {sorted.map((build) => (
                  <tr key={build.id} className="border-b last:border-0 hover:bg-accent/50">
                    <td className="px-4 py-2 font-mono">
                      <Link to="/builds/$buildId" params={{ buildId: String(build.id) }} className="hover:underline">
                        #{build.id}
                      </Link>
                    </td>
                    <td className="px-4 py-2">{build.jobName}</td>
                    <td className="px-4 py-2">
                      <Badge variant={buildStatusVariant(build.status)}>{build.status}</Badge>
                    </td>
                    <td className="px-4 py-2 text-muted-foreground">{formatTime(build.createdAt)}</td>
                    <td className="px-4 py-2 text-muted-foreground">
                      {formatDuration(build.startedAt, build.finishedAt)}
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
