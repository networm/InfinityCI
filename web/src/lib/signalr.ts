import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import { create } from "zustand";

export type ConnState = "connecting" | "connected" | "reconnecting" | "disconnected";

interface ConnectionStore {
  state: ConnState;
  setState: (state: ConnState) => void;
}

/** Live connection status; the layout shows a slim banner while degraded. */
export const useConnectionState = create<ConnectionStore>((set) => ({
  state: "connecting",
  setState: (state) => set({ state }),
}));

/**
 * One SignalR connection per browser tab.
 *
 * This module instance lives in the tab's own JS runtime, so every tab gets an
 * independent connection with its own ConnectionId and its own subscriptions —
 * tabs viewing different builds never affect each other. Do NOT move this into
 * a SharedWorker or storage-shared singleton; that would couple tabs together.
 */

let connection: HubConnection | null = null;
let starting: Promise<HubConnection> | null = null;

/**
 * Returns the tab's hub connection, (re)starting it when needed. Safe to call
 * repeatedly: while connected it resolves immediately; after a full drop it
 * restarts the same HubConnection instance so registered handlers survive.
 */
export function getCiHub(): Promise<HubConnection> {
  if (!connection) {
    starting ??= createAndStart();
    return starting;
  }
  if (connection.state === HubConnectionState.Disconnected) {
    // Automatic reconnect exhausted (or start failed earlier): try again.
    // The heartbeat watchdog keeps calling this and reloads after 5 failures.
    starting ??= connection
      .start()
      .then(() => {
        useConnectionState.getState().setState("connected");
        return connection!;
      })
      .finally(() => {
        starting = null;
      });
    return starting;
  }
  return Promise.resolve(connection);
}

async function createAndStart(): Promise<HubConnection> {
  const conn = new HubConnectionBuilder()
    .withUrl("/hubs/ci")
    // Exponential backoff: 0s, 1s, 2s, 4s, 8s, 16s, then a 30s cap. After
    // ~10 attempts this policy gives up (state → Disconnected) and the
    // heartbeat watchdog takes over (restart or page reload).
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds(context) {
        const attempt = context?.previousRetryCount ?? 0;
        if (attempt >= 10) return null;
        return attempt === 0 ? 0 : Math.min(1000 * 2 ** (attempt - 1), 30_000);
      },
    })
    .configureLogging(LogLevel.Warning)
    .build();

  conn.onreconnecting(() => useConnectionState.getState().setState("reconnecting"));
  conn.onreconnected(() => useConnectionState.getState().setState("connected"));
  conn.onclose(() => useConnectionState.getState().setState("disconnected"));

  try {
    await conn.start();
  } catch (err) {
    // Never cache a rejected start: a later getCiHub() call (e.g. from the
    // heartbeat) must be able to try again.
    starting = null;
    throw err;
  }
  useConnectionState.getState().setState("connected");
  connection = conn;
  return conn;
}
