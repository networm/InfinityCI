import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useParams } from "@tanstack/react-router";
import { useQueryClient } from "@tanstack/react-query";
import type { HubConnection } from "@microsoft/signalr";
import { Ban } from "lucide-react";

import { LogTerminal, type TerminalHandle } from "@/components/log-terminal";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";
import { buildStatusVariant, formatDuration, formatTime, stepStatusVariant } from "@/lib/format";
import { getCiHub } from "@/lib/signalr";
import type { Build } from "@/lib/types";

const textEncoder = new TextEncoder();
const byteLength = (text: string) => textEncoder.encode(text).length + 1; // + newline

export function BuildDetailPage() {
  const { buildId: buildIdParam } = useParams({ from: "/builds/$buildId" });
  const buildId = Number(buildIdParam);

  const [build, setBuild] = useState<Build | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [cancelling, setCancelling] = useState(false);
  const terminalRef = useRef<TerminalHandle>(null);
  const queryClient = useQueryClient();

  const versionRef = useRef(-1);
  const cursorRef = useRef(0);
  const terminal = terminalRef;

  const applyBuild = useCallback((next: Build) => {
    if (next.version >= versionRef.current) {
      versionRef.current = next.version;
      setBuild(next);
    }
  }, []);

  const applyLogLine = useCallback(
    (offset: number, text: string) => {
      if (offset < cursorRef.current) return; // duplicate from backfill/live overlap
      terminal.current?.writeLine(text);
      cursorRef.current = offset + byteLength(text);
    },
    [terminal],
  );

  useEffect(() => {
    let cancelled = false;
    let connection: HubConnection | null = null;

    const resubscribe = async () => {
      // After a reconnect, rejoin and backfill from the byte cursor: no lost or
      // duplicated lines regardless of what happened while we were offline.
      const sub = await connection!.invoke<{ build: Build; lines: { offset: number; text: string }[] }>(
        "SubscribeBuild",
        buildId,
        cursorRef.current,
      );
      if (cancelled) return;
      applyBuild(sub.build);
      for (const line of sub.lines) applyLogLine(line.offset, line.text);
    };

    (async () => {
      try {
        connection = await getCiHub();
        if (cancelled) return;

        connection.on("logAppended", (bid: number, offset: number, text: string) => {
          if (bid === buildId) applyLogLine(offset, text);
        });
        connection.on("buildUpdated", (b: Build) => {
          if (b.id === buildId) applyBuild(b);
        });
        connection.on("reconnected", () => {
          resubscribe().catch(() => {});
        });

        await resubscribe();
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e));
      }
    })();

    return () => {
      cancelled = true;
      connection?.off("logAppended");
      connection?.off("buildUpdated");
      connection?.invoke("UnsubscribeBuild", buildId).catch(() => {});
    };
  }, [applyBuild, applyLogLine, buildId]);

  const isRunning = build?.status === "Running" || build?.status === "Queued";

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <h1 className="text-lg font-semibold">
            <span className="font-mono">#{buildId}</span> {build?.jobName ?? ""}
          </h1>
          {build && <Badge variant={buildStatusVariant(build.status)}>{build.status}</Badge>}
        </div>
        <div className="flex items-center gap-3 text-xs text-muted-foreground">
          {build && (
            <span>
              started {formatTime(build.startedAt)} · {formatDuration(build.startedAt, build.finishedAt)}
              {build.exitCode !== null && build.status !== "Success" ? ` · exit ${build.exitCode}` : ""}
            </span>
          )}
          {isRunning && (
            <Button
              size="sm"
              variant="destructive"
              disabled={cancelling}
              onClick={async () => {
                setCancelling(true);
                try {
                  await api.cancel(buildId);
                  queryClient.invalidateQueries({ queryKey: ["jobs"] });
                } finally {
                  setCancelling(false);
                }
              }}
            >
              <Ban />
              Cancel
            </Button>
          )}
        </div>
      </div>

      {error && (
        <div className="rounded-md border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive">
          {error}
        </div>
      )}

      {build && (
        <div className="flex flex-wrap gap-2">
          {build.steps.map((step) => (
            <Badge key={step.name} variant={stepStatusVariant(step.status)}>
              {step.name}
              {step.exitCode !== null ? ` · ${step.exitCode}` : ""}
            </Badge>
          ))}
        </div>
      )}

      <div className="flex h-[60vh] flex-col overflow-hidden rounded-lg border">
        <div className="flex items-center justify-between border-b bg-secondary/50 px-3 py-1.5">
          <span className="text-xs font-medium text-muted-foreground">Console output</span>
          <button
            type="button"
            className="text-xs text-muted-foreground hover:text-foreground"
            onClick={() => terminal.current?.clear()}
          >
            Clear view
          </button>
        </div>
        <LogTerminal ref={terminalRef} className="h-full" />
      </div>

      <Link to="/" className="inline-block text-sm text-muted-foreground hover:text-foreground">
        ← Back to builds
      </Link>
    </div>
  );
}
