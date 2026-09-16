import { useEffect, useRef } from "react";
import type { DependencyList } from "react";
import type { HubConnection } from "@microsoft/signalr";

import { getCiHub } from "./signalr";

/**
 * Shared SignalR subscription primitives. Every page builds its live updates
 * from these instead of hand-rolling connection.on/invoke/off lifecycles:
 *
 *   useHubEvent       — attach a handler for a server-pushed event
 *   useReconnected    — replay snapshots after the connection recovers
 *   useHubGroup       — join/leave a group for the component's lifetime
 */

/** Attaches `handler` to `event` until the component unmounts. The latest
 * handler closure is used, so passing inline callbacks is safe. */
export function useHubEvent<A extends unknown[] = []>(event: string, handler: (...args: A) => void): void {
  const handlerRef = useRef(handler);
  useEffect(() => {
    handlerRef.current = handler;
  });

  useEffect(() => {
    let disposed = false;
    let detach: (() => void) | null = null;
    getCiHub()
      .then((connection) => {
        if (disposed) return;
        const listener = (...args: A) => handlerRef.current(...args);
        connection.on(event, listener);
        detach = () => connection.off(event, listener);
      })
      .catch(() => {
        // Connection recovery is the heartbeat watchdog's job.
      });
    return () => {
      disposed = true;
      detach?.();
    };
  }, [event]);
}

/** Runs `handler` whenever the underlying connection recovers from a drop.
 * Use it to replay a fresh snapshot (events missed while offline are gone). */
export function useReconnected(handler: () => void): void {
  const handlerRef = useRef(handler);
  useEffect(() => {
    handlerRef.current = handler;
  });

  useEffect(() => {
    let disposed = false;
    let detach: (() => void) | null = null;
    getCiHub()
      .then((connection) => {
        if (disposed) return;
        const listener = () => handlerRef.current();
        connection.on("reconnected", listener);
        detach = () => connection.off("reconnected", listener);
      })
      .catch(() => {});
    return () => {
      disposed = true;
      detach?.();
    };
  }, []);
}

/** Invokes `join` once on connect and `leave` on unmount/deps change; the
 * join return value (the group's initial snapshot) goes to `onSnapshot`.
 * Combine with useHubEvent for the group's live payloads. */
export function useHubGroup<S = unknown>(
  join: (connection: HubConnection) => Promise<S>,
  leave: (connection: HubConnection) => Promise<unknown>,
  deps: DependencyList = [],
  onSnapshot?: (snapshot: S) => void,
): void {
  const joinRef = useRef(join);
  const leaveRef = useRef(leave);
  const snapshotRef = useRef(onSnapshot);
  useEffect(() => {
    joinRef.current = join;
    leaveRef.current = leave;
    snapshotRef.current = onSnapshot;
  });

  useEffect(() => {
    let disposed = false;
    let connection: HubConnection | null = null;
    getCiHub()
      .then(async (conn) => {
        if (disposed) return;
        connection = conn;
        const snapshot = await joinRef.current(conn);
        if (!disposed) snapshotRef.current?.(snapshot);
      })
      .catch(() => {
        // Connection recovery is the heartbeat watchdog's job.
      });
    return () => {
      disposed = true;
      if (connection) void leaveRef.current(connection).catch(() => {});
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
}
